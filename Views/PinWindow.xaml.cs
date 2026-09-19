using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pinshot.Core;
using XamlAnimatedGif;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Pinshot.Views;

/// <summary>
/// Setuna 式静态贴图窗口：置顶显示截图，滚轮缩放、Ctrl+滚轮调透明度、
/// 双击收纳为小图、旋转/裁剪/画笔标注、拖放图片/文件/网页图创建新贴图，
/// 右键提取文字/翻译。窗口始终以物理像素跟踪贴图区域。
/// </summary>
public partial class PinWindow : Window
{
    private const double ShadowMarginDip = 8;
    private const double CompactSizeDip = 60;
    private const double MinScale = 0.05;
    private const double MaxScale = 16;
    private const double ZoomStep = 1.1;
    private const double MinOpacity = 0.1;
    private const double OpacityStep = 0.05;
    private static readonly Color ActiveGlowColor = Color.FromRgb(0x4D, 0x90, 0xFE);
    private static readonly Regex ImgSrcRegex = new("src=\"(?<src>https?://[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceUrlRegex = new(@"SourceURL:\s*(?<url>https?://\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum InteractionMode
    {
        None,
        Crop,
        Annotation,
    }

    private enum AnnotationTool
    {
        Pen,
        Text,
        Mosaic,
    }

    private readonly PinManager _manager;
    private DrawingBitmap _bitmap;
    private readonly byte[]? _gifBytes;
    private BitmapSource _displaySource;
    private readonly string? _sourcePath;

    /// <summary>缩放倍率为 1 时的贴图区域（物理像素），位置随拖动/缩放更新。</summary>
    private DrawingRectangle _baseBounds;

    /// <summary>收纳态下的窗口尺寸（物理像素），null 表示当前非收纳态。</summary>
    private DrawingRectangle? _compactSize;

    private double _scale = 1.0;
    private double _preCompactScale = 1.0;
    private double _userOpacity = 1.0;
    private string _borderStyle = "None";
    private string _borderColor = "#333333";
    private bool _shadowEnabled = true;
    private InteractionMode _mode = InteractionMode.None;
    private AnnotationTool _tool = AnnotationTool.Pen;
    private HwndSource? _hwndSource;
    private DrawingPoint _dragStartCursor;
    private DrawingRectangle _dragStartBounds;
    private bool _potentialDrag;
    private bool _isDragging;
    private bool _sourceInitialized;
    private bool _isClosing;
    private bool _toDustbox;
    private ContextMenu? _activeContextMenu;

    // 裁剪模式状态（DIP 坐标，相对裁剪层）
    private Point _cropStart;
    private Point _cropEnd;
    private bool _cropSelecting;

    private bool IsAnimated => _gifBytes != null;

    internal PinWindow(PinManager manager, DrawingBitmap bitmap, DrawingRectangle? physicalBounds, byte[]? gifBytes = null, string? sourcePath = null)
    {
        _manager = manager;
        _bitmap = bitmap;
        _gifBytes = gifBytes;
        _sourcePath = sourcePath;
        _displaySource = ToBitmapSource(bitmap);
        _baseBounds = ResolveInitialBounds(bitmap, physicalBounds);
        InitializeComponent();
        _borderStyle = App.Config.DefaultBorderStyle;
        ApplyBorder();
        if (gifBytes != null)
            AnimationBehavior.SetSourceStream(PART_Image, new MemoryStream(gifBytes));
        else
            PART_Image.Source = _displaySource;
        PART_TextBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
        PART_Ink.StrokeCollected += (_, e) => _steps.Add(new PenStrokeStep(e.Stroke));
        PART_AnnotationLayer.IsHitTestVisible = true;
        PART_AnnotationLayer.MouseLeftButtonDown += OnAnnotationTextMouseDown;
        PART_AnnotationLayer.MouseMove += OnAnnotationTextMouseMove;
        PART_AnnotationLayer.MouseLeftButtonUp += OnAnnotationTextMouseUp;
        LocationChanged += (_, _) => _toolbarWindow?.PlaceBelowOwner(4);
        ApplyBounds();
    }

    internal bool IsAnimatedGif => IsAnimated;

    internal byte[]? GifBytes => _gifBytes;

    internal BitmapSource DisplaySource => _displaySource;

    internal string? SourcePath => _sourcePath;

    internal System.Drawing.Size ImageSize => _bitmap.Size;

    private static DrawingRectangle ResolveInitialBounds(DrawingBitmap bitmap, DrawingRectangle? physicalBounds)
    {
        if (physicalBounds is { } bounds && bounds.Width > 0 && bounds.Height > 0)
            return bounds;

        // 选区坐标不可用时贴到光标附近
        if (Win32Helper.TryGetCursorPosition(out var cursor))
            return new DrawingRectangle(cursor.X, cursor.Y, bitmap.Width, bitmap.Height);

        var workArea = SystemParameters.WorkArea;
        return new DrawingRectangle((int)workArea.Left, (int)workArea.Top, bitmap.Width, bitmap.Height);
    }

    private DrawingRectangle ContentBounds => _compactSize is { } size
        ? new DrawingRectangle(_baseBounds.Left, _baseBounds.Top, size.Width, size.Height)
        : new DrawingRectangle(
            _baseBounds.Left,
            _baseBounds.Top,
            Math.Max(1, (int)Math.Round(_baseBounds.Width * _scale)),
            Math.Max(1, (int)Math.Round(_baseBounds.Height * _scale)));

    #region 截图避让

    internal void SetCloaked(bool cloaked) => Win32Helper.SetWindowCloaked(this, cloaked);

    internal void CloseTransientUiForCapture()
    {
        if (_activeContextMenu is { IsOpen: true })
            _activeContextMenu.IsOpen = false;
    }

    #endregion

    /// <summary>Setuna 托盘“激活所有贴图”：重新提到最前并激活。</summary>
    internal void BringToFront()
    {
        Topmost = false;
        Topmost = true;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _sourceInitialized = true;
        Win32Helper.HideFromAltTab(this);
        _hwndSource = WndProcHelper.AddWndProcHook(this, WndProc);
        ApplyBounds();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _manager.NotifyActivated(this);
        UpdateFocusVisual(isActive: true);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        UpdateFocusVisual(isActive: false);
    }

    private void UpdateFocusVisual(bool isActive)
    {
        PART_WindowEffect.Color = isActive ? ActiveGlowColor : Colors.Black;
        PART_WindowEffect.BlurRadius = isActive ? 12 : 10;
        PART_WindowEffect.Opacity = isActive ? 0.68 : 0.35;
        PART_WindowEffect.ShadowDepth = isActive ? 0 : 2;
    }

    #region 鼠标交互

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_isClosing)
            return;
        if (_mode == InteractionMode.Annotation && _tool == AnnotationTool.Text &&
            !IsOverToolbar(e.OriginalSource))
        {
            // 点击的是已提交文本 → 不放置新输入框，放行给覆盖层做拖动
            if (IsOverAnnotationText(e.OriginalSource))
            {
                CommitActiveText();
                return;
            }
            e.Handled = true;
            CommitActiveText();
            ShowTextBoxAt(e.GetPosition(PART_Surface));
            return;
        }
        if (_mode != InteractionMode.None)
            return;
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            // 双击动作可配置（Setuna：收缩 / 关闭）
            if (App.Config.DoubleClickAction.Equals("Close", StringComparison.OrdinalIgnoreCase))
            {
                Close();
                return;
            }
            if (Win32Helper.TryGetCursorPosition(out var cursor))
                ToggleCompact(cursor);
            return;
        }

        Focus();
        if (!Win32Helper.TryGetCursorPosition(out _dragStartCursor))
            return;
        _dragStartBounds = _baseBounds;
        _potentialDrag = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_potentialDrag || e.LeftButton != MouseButtonState.Pressed ||
            !Win32Helper.TryGetCursorPosition(out var cursor))
            return;
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        var dpi = GetDpi();
        if (!_isDragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
            return;
        if (!_isDragging)
        {
            _isDragging = true;
            // Setuna 行为：拖动时贴图保持半透明，便于看清底下的内容
            if (App.Config.DragSemiTransparent)
                Opacity = 0.55;
        }
        _baseBounds = new DrawingRectangle(
            _dragStartBounds.Left + dx, _dragStartBounds.Top + dy,
            _dragStartBounds.Width, _dragStartBounds.Height);
        SetPhysicalWindowBounds(GetDpi());
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_potentialDrag)
            return;
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_potentialDrag)
            FinishDrag();
    }

    private void FinishDrag()
    {
        var moved = _isDragging;
        _potentialDrag = _isDragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        Opacity = _userOpacity;
        if (moved)
            _manager.NotifyStateChanged();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_isClosing || _mode != InteractionMode.None)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var delta = OpacityStep * Math.Sign(e.Delta);
            SetOpacity(Opacity + delta);
            e.Handled = true;
            return;
        }

        if (_compactSize != null)
            return;
        if (Win32Helper.TryGetCursorPosition(out var cursor))
            ZoomAt(cursor.X, cursor.Y, e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
        e.Handled = true;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (_isClosing)
            return;
        OpenContextMenu();
        e.Handled = true;
    }

    #endregion

    #region 拖放：图片文件 / 位图 / 网页图片 → 新贴图（Setuna 行为）

    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        e.Effects = HasAcceptableDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (!HasAcceptableDrop(e.Data))
            return;
        e.Handled = true;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                _ = _manager.AddFromFilesAsync(files);
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                 e.Data.GetData(DataFormats.Bitmap) is DrawingBitmap droppedBitmap)
        {
            _manager.CreatePin((DrawingBitmap)droppedBitmap.Clone());
        }
        else if (ExtractImageUrl(e.Data) is { } url)
        {
            _ = _manager.AddFromWebAsync(url);
        }
    }

    private static bool HasAcceptableDrop(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ||
        data.GetDataPresent(DataFormats.Bitmap) ||
        ExtractImageUrl(data) != null;

    /// <summary>从拖放数据提取网页图片地址：优先 HTML 剪贴板的 SourceURL，其次 img src。</summary>
    private static string? ExtractImageUrl(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.Html) &&
            data.GetData(DataFormats.Html) is string html)
        {
            var sourceUrl = SourceUrlRegex.Match(html).Groups["url"].Value;
            if (IsHttpUrl(sourceUrl))
                return sourceUrl;
            var src = ImgSrcRegex.Match(html).Groups["src"].Value;
            if (IsHttpUrl(src))
                return src;
        }
        if (data.GetDataPresent(DataFormats.Text) &&
            data.GetData(DataFormats.Text) is string text &&
            IsHttpUrl(text))
            return text.Trim();
        return null;
    }

    private static bool IsHttpUrl([NotNullWhen(true)] string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    #endregion

    #region 键盘交互

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_isClosing || _activeContextMenu is { IsOpen: true })
            return;

        // 图片标注模式
        if (_mode == InteractionMode.Annotation)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (PART_TextBox.Visibility == Visibility.Visible)
                    HideActiveTextBox();
                else
                    ExitAnnotation();
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                UndoAnnotation();
            }
            else if (e.Key == Key.Enter && PART_TextBox.Visibility != Visibility.Visible)
            {
                e.Handled = true;
                DoneAnnotation();
            }
            return;
        }

        // 裁剪模式
        if (_mode == InteractionMode.Crop)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelCrop();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyCrop();
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                // 优先收起译文覆盖层（微信式），再次 Esc 才关闭贴图
                if (PART_TranslateLayer.Visibility == Visibility.Visible)
                {
                    ToggleTranslateOverlay();
                    return;
                }
                Close();
                return;
            case Key.Enter when _compactSize != null:
                if (Win32Helper.TryGetCursorPosition(out var cursor))
                {
                    e.Handled = true;
                    ToggleCompact(cursor);
                }
                return;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                RotateRight90();
                return;
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                e.Handled = true;
                OpenContextMenu();
                return;
            case Key.OemPlus or Key.Add:
                e.Handled = true;
                ZoomAtCenter(ZoomStep);
                return;
            case Key.OemMinus or Key.Subtract:
                e.Handled = true;
                ZoomAtCenter(1 / ZoomStep);
                return;
        }

        if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var step = Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0),
                Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
            };
            if (dx == 0 && dy == 0)
                return;
            _baseBounds.Offset(dx, dy);
            ApplyBounds();
            e.Handled = true;
        }
    }

    #endregion

    #region 缩放 / 透明度 / 收纳

    private void ZoomAt(int cursorX, int cursorY, double factor)
    {
        var newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - _scale) < double.Epsilon)
            return;
        var content = ContentBounds;
        // 光标处的基准像素点，缩放后仍保持在光标下方
        var relX = (cursorX - content.Left) / _scale;
        var relY = (cursorY - content.Top) / _scale;
        _scale = newScale;
        _baseBounds = new DrawingRectangle(
            (int)Math.Round(cursorX - relX * newScale),
            (int)Math.Round(cursorY - relY * newScale),
            _baseBounds.Width, _baseBounds.Height);
        ApplyBounds();
        _manager.NotifyStateChanged();
    }

    private void ZoomAtCenter(double factor)
    {
        var content = ContentBounds;
        ZoomAt(content.Left + content.Width / 2, content.Top + content.Height / 2, factor);
    }

    internal void SetOpacity(double value)
    {
        _userOpacity = Math.Clamp(value, MinOpacity, 1.0);
        Opacity = _userOpacity;
    }

    /// <summary>双击在“正常贴图 ⇄ 60×60 收纳小图”间切换，切换均以光标为中心。</summary>
    private void ToggleCompact(DrawingPoint cursor)
    {
        if (_compactSize is { } size)
        {
            // 还原：以收纳小图中心为中心展开
            var centerX = _baseBounds.Left + size.Width / 2;
            var centerY = _baseBounds.Top + size.Height / 2;
            _scale = _preCompactScale;
            _compactSize = null;
            PART_Frame.Visibility = Visibility.Collapsed;
            _baseBounds = new DrawingRectangle(
                (int)Math.Round(centerX - _baseBounds.Width * _scale / 2),
                (int)Math.Round(centerY - _baseBounds.Height * _scale / 2),
                _baseBounds.Width, _baseBounds.Height);
        }
        else
        {
            _preCompactScale = _scale;
            var dpi = GetDpi();
            _compactSize = new DrawingRectangle(0, 0,
                Math.Max(1, (int)Math.Ceiling(CompactSizeDip * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(CompactSizeDip * dpi.DpiScaleY)));
            _baseBounds = new DrawingRectangle(
                cursor.X - _compactSize.Value.Width / 2,
                cursor.Y - _compactSize.Value.Height / 2,
                _baseBounds.Width, _baseBounds.Height);
            PART_Frame.Visibility = Visibility.Visible;
        }
        ApplyBounds();
        _manager.NotifyStateChanged();
    }

    #endregion

    #region 旋转 / 裁剪 / 画笔标注

    /// <summary>顺时针旋转 90°：直接烘焙进位图（OCR/保存/复制保持一致），窗口以内容中心为轴换向。</summary>
    internal void RotateRight90()
    {
        if (IsAnimated)
            return;
        var content = ContentBounds;
        var centerX = content.Left + content.Width / 2.0;
        var centerY = content.Top + content.Height / 2.0;

        var rotated = (DrawingBitmap)_bitmap.Clone();
        rotated.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone);
        _bitmap.Dispose();
        _bitmap = rotated;
        RefreshDisplaySource();

        _baseBounds = new DrawingRectangle(
            _baseBounds.X, _baseBounds.Y, _bitmap.Width, _bitmap.Height);
        var newContent = ContentBounds;
        _baseBounds.Offset(
            (int)Math.Round(centerX - (newContent.Left + newContent.Width / 2.0)),
            (int)Math.Round(centerY - (newContent.Top + newContent.Height / 2.0)));
        ApplyBounds();
        _manager.NotifyStateChanged();
    }

    /// <summary>向左旋转 90°（Setuna“向左旋转”）。</summary>
    internal void RotateLeft90()
    {
        if (IsAnimated)
            return;
        var content = ContentBounds;
        var centerX = content.Left + content.Width / 2.0;
        var centerY = content.Top + content.Height / 2.0;

        var rotated = (DrawingBitmap)_bitmap.Clone();
        rotated.RotateFlip(System.Drawing.RotateFlipType.Rotate270FlipNone);
        _bitmap.Dispose();
        _bitmap = rotated;
        RefreshDisplaySource();

        _baseBounds = new DrawingRectangle(
            _baseBounds.X, _baseBounds.Y, _bitmap.Width, _bitmap.Height);
        var newContent = ContentBounds;
        _baseBounds.Offset(
            (int)Math.Round(centerX - (newContent.Left + newContent.Width / 2.0)),
            (int)Math.Round(centerY - (newContent.Top + newContent.Height / 2.0)));
        ApplyBounds();
        _manager.NotifyStateChanged();
    }

    /// <summary>垂直翻转（Setuna 样式项）。</summary>
    internal void FlipVertical()
    {
        if (IsAnimated)
            return;
        var flipped = (DrawingBitmap)_bitmap.Clone();
        flipped.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
        _bitmap.Dispose();
        _bitmap = flipped;
        RefreshDisplaySource();
        _manager.NotifyStateChanged();
    }

    /// <summary>水平翻转（Setuna 样式项）。</summary>
    internal void FlipHorizontal()
    {
        if (IsAnimated)
            return;
        var flipped = (DrawingBitmap)_bitmap.Clone();
        flipped.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
        _bitmap.Dispose();
        _bitmap = flipped;
        RefreshDisplaySource();
        _manager.NotifyStateChanged();
    }

    /// <summary>缩放到指定倍率（Setuna“缩放为 N%”），以内容中心为锚。</summary>
    internal void ZoomTo(double targetScale)
    {
        if (_compactSize != null)
            return;
        var content = ContentBounds;
        ZoomAt(content.Left + content.Width / 2, content.Top + content.Height / 2,
            Math.Clamp(targetScale, MinScale, MaxScale) / _scale);
    }

    /// <summary>应用边框样式（无边框/单色边框）。</summary>
    internal void SetBorder(string style)
    {
        _borderStyle = style;
        ApplyBorder();
        _manager.NotifyStateChanged();
    }

    private void ApplyBorder()
    {
        // 仅两种边框：无边框 / 单色边框（2px，保证肉眼可辨）
        var hasBorder = _borderStyle != "None";
        PART_BorderMono.Visibility = hasBorder ? Visibility.Visible : Visibility.Collapsed;
        PART_BorderMono.BorderBrush = BrushFromHex(_borderColor);
        // DropShadowEffect 是软件渲染，大位图（>120 万像素）会造成明显卡顿/假死 —— 大图强制关闭
        var tooBigForShadow = (long)_bitmap.Width * _bitmap.Height > 1_200_000;
        PART_Surface.Effect = _shadowEnabled && !tooBigForShadow ? PART_WindowEffect : null;
    }

    /// <summary>选择边框颜色（Setuna“边框颜色”，选择后自动切换为单色边框）。</summary>
    internal void SetBorderColor(string hex)
    {
        _borderColor = hex;
        _borderStyle = "Mono";
        ApplyBorder();
        _manager.NotifyStateChanged();
    }

    /// <summary>窗口阴影开关（Setuna“窗口阴影”）。</summary>
    private void SetShadow(bool enabled)
    {
        _shadowEnabled = enabled;
        ApplyBorder();
        _manager.NotifyStateChanged();
    }

    private static Brush BrushFromHex(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // 非法颜色回退默认
        }
        return Brushes.DimGray;
    }

    /// <summary>粘贴：用剪贴板图片新建贴图（Setuna 样式项）。</summary>
    private void PasteFromClipboard()
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } clipboardImage)
            return;
        try
        {
            var gdi = ImageLoader.BitmapFromSource(clipboardImage);
            _manager.CreatePin(gdi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"粘贴失败：{ex.Message}", "Pinshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>剪切：复制当前贴图并关闭（Setuna 样式项）。</summary>
    private void CutPin()
    {
        try
        {
            Clipboard.SetImage(_displaySource);
        }
        catch
        {
            // 复制失败仍然关闭
        }
        Close();
    }

    /// <summary>托盘批量入口：复制到剪贴板。</summary>
    internal void CopyToClipboard() => CopyImage();

    /// <summary>托盘批量入口：剪切（复制后关闭）。</summary>
    internal void CutToClipboard() => CutPin();

    /// <summary>托盘批量入口：弹出另存为对话框。</summary>
    internal void SaveAsDialog() => SaveAs();

    /// <summary>托盘批量入口：在文件夹中查看。</summary>
    internal void Reveal() => RevealInFolder();

    /// <summary>托盘批量入口：进入图片标注。</summary>
    internal void StartAnnotation() => EnterAnnotation(AnnotationTool.Pen);

    /// <summary>托盘批量入口：销毁（进回收站）。</summary>
    internal void Destroy() => DestroyToDustbox();

    /// <summary>托盘批量入口：全部透明度设为指定值。</summary>
    internal void ApplyOpacity(double value) => SetOpacity(value);

    /// <summary>托盘批量入口：缩放到指定倍率。</summary>
    internal void ApplyZoom(double targetScale) => ZoomTo(targetScale);

    private bool _selectionWired;

    private void WireSelectionLayer()
    {
        if (_selectionWired)
            return;
        _selectionWired = true;
        PART_CropLayer.Visibility = Visibility.Visible;
        PART_CropMask.Visibility = Visibility.Collapsed;
        PART_CropLayer.PreviewMouseLeftButtonDown += OnCropMouseDown;
        PART_CropLayer.PreviewMouseMove += OnCropMouseMove;
        PART_CropLayer.PreviewMouseLeftButtonUp += OnCropMouseUp;
    }

    private void UnwireSelectionLayer()
    {
        if (!_selectionWired)
            return;
        _selectionWired = false;
        _cropSelecting = false;
        PART_CropLayer.Visibility = Visibility.Collapsed;
        PART_CropMask.Visibility = Visibility.Collapsed;
        PART_CropLayer.PreviewMouseLeftButtonDown -= OnCropMouseDown;
        PART_CropLayer.PreviewMouseMove -= OnCropMouseMove;
        PART_CropLayer.PreviewMouseLeftButtonUp -= OnCropMouseUp;
    }

    private void ExitSelectionMode()
    {
        _mode = InteractionMode.None;
        UnwireSelectionLayer();
    }

    private void EnterCropMode()
    {
        if (IsAnimated || _compactSize != null)
            return;
        _mode = InteractionMode.Crop;
        WireSelectionLayer();
        Focus();
    }

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(PART_CropLayer);
        _cropEnd = _cropStart;
        _cropSelecting = true;
        PART_CropMask.Visibility = Visibility.Visible;
        UpdateCropRect();
        PART_CropLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropSelecting)
            return;
        _cropEnd = e.GetPosition(PART_CropLayer);
        UpdateCropRect();
        e.Handled = true;
    }

    private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropSelecting)
            return;
        _cropSelecting = false;
        _cropEnd = e.GetPosition(PART_CropLayer);
        UpdateCropRect();
        if (PART_CropLayer.IsMouseCaptured)
            PART_CropLayer.ReleaseMouseCapture();
        // 马赛克工具：松手立即应用一步，可继续涂下一块；裁剪模式：等待 Enter 确认
        if (_mode == InteractionMode.Annotation && _tool == AnnotationTool.Mosaic)
            ApplyMosaicStep();
        e.Handled = true;
    }

    private void UpdateCropRect()
    {
        var x = Math.Min(_cropStart.X, _cropEnd.X);
        var y = Math.Min(_cropStart.Y, _cropEnd.Y);
        var w = Math.Abs(_cropStart.X - _cropEnd.X);
        var h = Math.Abs(_cropStart.Y - _cropEnd.Y);
        Canvas.SetLeft(PART_CropMask, x);
        Canvas.SetTop(PART_CropMask, y);
        PART_CropRect.Width = Math.Max(0, w);
        PART_CropRect.Height = Math.Max(0, h);
    }

    private void ApplyCrop()
    {
        var dpi = GetDpi();
        var x = Math.Min(_cropStart.X, _cropEnd.X) * dpi.DpiScaleX;
        var y = Math.Min(_cropStart.Y, _cropEnd.Y) * dpi.DpiScaleY;
        var w = Math.Abs(_cropStart.X - _cropEnd.X) * dpi.DpiScaleX;
        var h = Math.Abs(_cropStart.Y - _cropEnd.Y) * dpi.DpiScaleY;
        ExitSelectionMode();
        if (w < 4 || h < 4)
            return; // 选区过小视为取消

        var content = ContentBounds;
        // 选区（物理像素）→ 基准像素 → GDI 裁剪
        var cropRect = new System.Drawing.Rectangle(
            (int)Math.Floor(x / _scale),
            (int)Math.Floor(y / _scale),
            (int)Math.Ceiling(w / _scale),
            (int)Math.Ceiling(h / _scale));
        cropRect.Intersect(new System.Drawing.Rectangle(0, 0, _bitmap.Width, _bitmap.Height));
        if (cropRect.Width < 4 || cropRect.Height < 4)
            return;

        var cropped = _bitmap.Clone(cropRect, _bitmap.PixelFormat);
        _bitmap.Dispose();
        _bitmap = cropped;
        RefreshDisplaySource();
        _scale = 1;
        _baseBounds = new DrawingRectangle(
            (int)Math.Round(content.Left + x),
            (int)Math.Round(content.Top + y),
            _bitmap.Width, _bitmap.Height);
        ApplyBounds();
        _manager.NotifyStateChanged();
    }

    private void CancelCrop() => ExitSelectionMode();

    #region 图片标注（画笔 / 文本 / 马赛克，微信截图式工具条）

    private abstract record AnnotationStep;

    private sealed record PenStrokeStep(System.Windows.Ink.Stroke Stroke) : AnnotationStep;

    private sealed record TextStep(TextBlock Element) : AnnotationStep;

    private sealed record MosaicStep(System.Drawing.Rectangle Rect) : AnnotationStep;

    private static readonly (string Name, string Hex)[] AnnotationColors =
    [
        ("红色", "#E81123"), ("橙色", "#FF8C00"), ("黄色", "#FFD700"), ("绿色", "#107C10"),
        ("蓝色", "#0078D7"), ("紫色", "#8764B8"), ("黑色", "#333333"), ("白色", "#FFFFFF"),
    ];

    private List<AnnotationStep> _steps = [];
    private DrawingBitmap? _annotationBackup;
    private int _annotationColorIndex;
    private int _annotationSizeLevel = 1;

    private double PenWidthDip => _annotationSizeLevel switch { 0 => 2, 1 => 4, _ => 8 };

    private double TextSizeDip => _annotationSizeLevel switch { 0 => 14, 1 => 18, _ => 26 };

    private int MosaicBlock => _annotationSizeLevel switch { 0 => 8, 1 => 12, _ => 20 };

    private void EnterAnnotation(AnnotationTool tool)
    {
        if (IsAnimated || _compactSize != null)
            return;
        if (_mode == InteractionMode.Annotation)
        {
            CommitActiveText();
            _tool = tool;
            ApplyAnnotationUi();
            return;
        }

        _annotationBackup = (DrawingBitmap)_bitmap.Clone();
        _mode = InteractionMode.Annotation;
        _tool = tool;
        _steps = [];
        PART_Ink.Strokes.Clear();
        PART_AnnotationLayer.Children.Clear();
        HideActiveTextBox();
        PART_AnnotationLayer.Visibility = Visibility.Visible;
        EnsureToolbar();
        _toolbarWindow?.Show();
        _toolbarWindow?.PlaceBelowOwner(4);
        ApplyAnnotationUi();
        Focus();
    }

    /// <summary>按当前工具 / 粗细 / 颜色刷新各层与工具条状态。</summary>
    private void ApplyAnnotationUi()
    {
        // 笔迹层始终可见（画笔/文字/马赛克标注共存展示）；
        // 只有画笔工具开启绘制，其他工具时笔迹不可编辑但不消失
        PART_Ink.Visibility = Visibility.Visible;
        PART_Ink.EditingMode = _tool == AnnotationTool.Pen ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        PART_Ink.DefaultDrawingAttributes.Color = (Color)ColorConverter.ConvertFromString(
            AnnotationColors[_annotationColorIndex].Hex);
        var dpi = GetDpi();
        PART_Ink.DefaultDrawingAttributes.Width = PenWidthDip * dpi.DpiScaleX;
        PART_Ink.DefaultDrawingAttributes.Height = PenWidthDip * dpi.DpiScaleY;
        PART_Ink.DefaultDrawingAttributes.FitToCurve = true;

        if (_tool == AnnotationTool.Mosaic)
            WireSelectionLayer();
        else
            UnwireSelectionLayer();

        if (PART_TextBox.Visibility == Visibility.Visible)
            ApplyTextBoxStyle();
        UpdateToolbarState();
    }

    private void ApplyTextBoxStyle()
    {
        PART_TextBox.FontSize = TextSizeDip;
        PART_TextBox.FontWeight = FontWeights.Bold;
        PART_TextBox.FontFamily = new FontFamily("Microsoft YaHei");
        PART_TextBox.Foreground = BrushFromHex(AnnotationColors[_annotationColorIndex].Hex);
        PART_TextBox.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        PART_TextBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        PART_TextBox.BorderThickness = new Thickness(1);
        PART_TextBox.CaretBrush = Brushes.White;
    }

    private void ShowTextBoxAt(Point position)
    {
        PART_TextBox.Margin = new Thickness(position.X, position.Y, 0, 0);
        PART_TextBox.Text = string.Empty;
        ApplyTextBoxStyle();
        PART_TextBox.Visibility = Visibility.Visible;
        PART_TextBox.Focus();
    }

    private void HideActiveTextBox()
    {
        PART_TextBox.Visibility = Visibility.Collapsed;
        PART_TextBox.Text = string.Empty;
    }

    #region 文字拖动

    private System.Windows.Controls.TextBlock? _dragText;
    private Point _dragTextOrigin;
    private Point _dragTextStart;

    private void OnAnnotationTextMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只拖已提交的文本元素；命中 InkCanvas 笔迹/马赛克选区时不拦截
        if (e.OriginalSource is not System.Windows.Controls.TextBlock text ||
            text.Parent is not System.Windows.Controls.Canvas)
            return;
        _dragText = text;
        _dragTextOrigin = e.GetPosition(PART_AnnotationLayer);
        _dragTextStart = new Point(
            Canvas.GetLeft(text), Canvas.GetTop(text));
        PART_AnnotationLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnAnnotationTextMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragText == null || !PART_AnnotationLayer.IsMouseCaptured)
            return;
        var position = e.GetPosition(PART_AnnotationLayer);
        Canvas.SetLeft(_dragText, _dragTextStart.X + position.X - _dragTextOrigin.X);
        Canvas.SetTop(_dragText, _dragTextStart.Y + position.Y - _dragTextOrigin.Y);
        e.Handled = true;
    }

    private void OnAnnotationTextMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragText == null)
            return;
        _dragText = null;
        if (PART_AnnotationLayer.IsMouseCaptured)
            PART_AnnotationLayer.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    /// <summary>把正在输入的文本固化为覆盖层元素（步骤可撤回，完成时统一烘焙）。</summary>
    private void CommitActiveText()
    {
        if (PART_TextBox.Visibility != Visibility.Visible || string.IsNullOrEmpty(PART_TextBox.Text))
        {
            HideActiveTextBox();
            return;
        }
        var element = new TextBlock
        {
            Text = PART_TextBox.Text,
            FontSize = TextSizeDip,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = BrushFromHex(AnnotationColors[_annotationColorIndex].Hex),
        };
        Canvas.SetLeft(element, PART_TextBox.Margin.Left);
        Canvas.SetTop(element, PART_TextBox.Margin.Top);
        PART_AnnotationLayer.Children.Add(element);
        _steps.Add(new TextStep(element));
        HideActiveTextBox();
    }

    private void ApplyMosaicStep()
    {
        var dpi = GetDpi();
        var x = Math.Min(_cropStart.X, _cropEnd.X) * dpi.DpiScaleX;
        var y = Math.Min(_cropStart.Y, _cropEnd.Y) * dpi.DpiScaleY;
        var w = Math.Abs(_cropStart.X - _cropEnd.X) * dpi.DpiScaleX;
        var h = Math.Abs(_cropStart.Y - _cropEnd.Y) * dpi.DpiScaleY;
        PART_CropMask.Visibility = Visibility.Collapsed;
        _cropStart = _cropEnd = default;
        if (w < 4 || h < 4)
            return;

        var rect = new System.Drawing.Rectangle(
            (int)Math.Floor(x / _scale), (int)Math.Floor(y / _scale),
            (int)Math.Ceiling(w / _scale), (int)Math.Ceiling(h / _scale));
        rect.Intersect(new System.Drawing.Rectangle(0, 0, _bitmap.Width, _bitmap.Height));
        if (rect.Width < 2 || rect.Height < 2)
            return;

        Pixelate(rect, refresh: true);
        _steps.Add(new MosaicStep(rect));
    }

    /// <summary>对位图区域打马赛克（块大小跟随粗细设置）。</summary>
    private void Pixelate(System.Drawing.Rectangle rect, bool refresh)
    {
        using var region = new DrawingBitmap(rect.Width, rect.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(region))
        {
            g.DrawImage(_bitmap, new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height),
                rect, System.Drawing.GraphicsUnit.Pixel);
        }
        var block = MosaicBlock;
        using var small = new DrawingBitmap(
            Math.Max(1, rect.Width / block), Math.Max(1, rect.Height / block),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.DrawImage(region, 0, 0, small.Width, small.Height);
        }
        using var big = new DrawingBitmap(rect.Width, rect.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(small, 0, 0, rect.Width, rect.Height);
        }
        using (var g = System.Drawing.Graphics.FromImage(_bitmap))
        {
            g.DrawImage(big, rect.X, rect.Y, rect.Width, rect.Height);
        }
        if (refresh)
            RefreshDisplaySource();
    }

    /// <summary>撤回上一步（画笔一笔 / 一段文字 / 一块马赛克）。</summary>
    private void UndoAnnotation()
    {
        if (_steps.Count == 0)
            return;
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        switch (step)
        {
            case PenStrokeStep pen:
                PART_Ink.Strokes.Remove(pen.Stroke);
                break;
            case TextStep text:
                PART_AnnotationLayer.Children.Remove(text.Element);
                break;
            case MosaicStep:
                // 马赛克不可逆：从备份重建位图并按剩余步骤重放
                _bitmap.Dispose();
                _bitmap = (DrawingBitmap)_annotationBackup!.Clone();
                foreach (var mosaic in _steps.OfType<MosaicStep>())
                    Pixelate(mosaic.Rect, refresh: false);
                RefreshDisplaySource();
                break;
        }
    }

    /// <summary>退出（取消所有修改）：位图回滚到进入标注前。</summary>
    private void ExitAnnotation()
    {
        if (_annotationBackup != null)
        {
            _bitmap.Dispose();
            _bitmap = (DrawingBitmap)_annotationBackup.Clone();
            RefreshDisplaySource();
        }
        CleanupAnnotation();
    }

    /// <summary>完成：画笔与文本烘焙进位图，马赛克已生效。</summary>
    private void DoneAnnotation()
    {
        CommitActiveText();
        if (_steps.Count > 0)
        {
            Cursor = Cursors.Wait;
            var width = _bitmap.Width;
            var height = _bitmap.Height;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_displaySource, new Rect(0, 0, width, height));
                // 笔迹与文本层按基准像素等比映射烘焙
                dc.DrawRectangle(new VisualBrush(PART_Ink), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(PART_AnnotationLayer), null, new Rect(0, 0, width, height));
            }
            var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            rendered.Freeze();

            var baked = ImageLoader.BitmapFromSource(rendered);
            _bitmap.Dispose();
            _bitmap = baked;
            RefreshDisplaySource();
            _manager.NotifyStateChanged();
        }
        Cursor = Cursors.Arrow;
        CleanupAnnotation();
    }

    private void CleanupAnnotation()
    {
        _annotationBackup?.Dispose();
        _annotationBackup = null;
        _steps.Clear();
        PART_Ink.Strokes.Clear();
        PART_AnnotationLayer.Children.Clear();
        PART_AnnotationLayer.Visibility = Visibility.Collapsed;
        PART_Ink.Visibility = Visibility.Collapsed;
        HideActiveTextBox();
        _toolbarWindow?.Close();
        _toolbarWindow = null;
        UnwireSelectionLayer();
        if (_mode == InteractionMode.Annotation)
            _mode = InteractionMode.None;
    }

    /// <summary>点击是否落在已提交的文本标注上（用于拖动放行）。</summary>
    private bool IsOverAnnotationText(object eventSource)
    {
        var current = eventSource as DependencyObject;
        while (current != null)
        {
            if (current is System.Windows.Controls.TextBlock text &&
                ReferenceEquals(text.Parent, PART_AnnotationLayer))
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool IsOverToolbar(object eventSource)
    {
        if (_toolbarWindow == null || eventSource is not DependencyObject source)
            return false;
        var current = source;
        while (current != null)
        {
            if (current is AnnotationToolbarWindow)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Enter 提交这段文字，可继续点击放置下一段
            e.Handled = true;
            CommitActiveText();
        }
        else if (e.Key == Key.Escape)
        {
            // Esc 只取消当前输入框，再按 Esc 才退出标注
            e.Handled = true;
            HideActiveTextBox();
        }
    }

    #region 工具条（独立悬浮窗口）

    private AnnotationToolbarWindow? _toolbarWindow;

    private void EnsureToolbar()
    {
        if (_toolbarWindow != null)
            return;
        var toolbar = new AnnotationToolbarWindow(this);
        toolbar.PenButton.Click += (_, _) => SwitchTool(AnnotationTool.Pen);
        toolbar.TextButton.Click += (_, _) => SwitchTool(AnnotationTool.Text);
        toolbar.MosaicButton.Click += (_, _) => SwitchTool(AnnotationTool.Mosaic);
        toolbar.SizeSmallButton.Click += (_, _) => SetSizeLevel(0);
        toolbar.SizeMidButton.Click += (_, _) => SetSizeLevel(1);
        toolbar.SizeLargeButton.Click += (_, _) => SetSizeLevel(2);
        toolbar.UndoButton.Click += (_, _) => UndoAnnotation();
        toolbar.ExitButton.Click += (_, _) => ExitAnnotation();
        toolbar.DoneButton.Click += (_, _) => DoneAnnotation();

        for (var i = 0; i < AnnotationColors.Length; i++)
        {
            var index = i;
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Background = BrushFromHex(AnnotationColors[i].Hex),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = AnnotationColors[i].Name,
                Tag = index,
            };
            swatch.MouseLeftButtonDown += (_, _) => SetColorIndex(index);
            toolbar.ColorPanel.Children.Add(swatch);
        }

        _toolbarWindow = toolbar;
        toolbar.ContentRendered += (_, _) => toolbar.PlaceBelowOwner(4);
    }

    private void SwitchTool(AnnotationTool tool)
    {
        if (_mode != InteractionMode.Annotation)
            return;
        CommitActiveText();
        _tool = tool;
        ApplyAnnotationUi();
    }

    private void SetSizeLevel(int level)
    {
        if (_mode != InteractionMode.Annotation)
            return;
        _annotationSizeLevel = level;
        ApplyAnnotationUi();
    }

    private void SetColorIndex(int index)
    {
        if (_mode != InteractionMode.Annotation)
            return;
        _annotationColorIndex = index;
        ApplyAnnotationUi();
    }

    private void UpdateToolbarState()
    {
        if (_toolbarWindow is not { } toolbar)
            return;
        SetActive(toolbar.PenButton, _tool == AnnotationTool.Pen);
        SetActive(toolbar.TextButton, _tool == AnnotationTool.Text);
        SetActive(toolbar.MosaicButton, _tool == AnnotationTool.Mosaic);
        SetActive(toolbar.SizeSmallButton, _annotationSizeLevel == 0);
        SetActive(toolbar.SizeMidButton, _annotationSizeLevel == 1);
        SetActive(toolbar.SizeLargeButton, _annotationSizeLevel == 2);
        foreach (var child in toolbar.ColorPanel.Children.OfType<Border>())
            child.BorderBrush = (int)child.Tag == _annotationColorIndex ? Brushes.White : Brushes.Transparent;
        toolbar.PlaceBelowOwner(4);
        return;

        static void SetActive(Button button, bool active)
        {
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            button.Background = active
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xEA, 0xF2, 0xFF)) // 淡蓝选中底
                : Brushes.Transparent;
        }
    }

    #endregion

    #endregion

    private void RefreshDisplaySource()
    {
        // 位图发生变化（旋转/裁剪/标注/马赛克），原位译文一律作废
        ClearTranslation();
        _displaySource = ToBitmapSource(_bitmap);
        if (!IsAnimated)
            PART_Image.Source = _displaySource;
    }

    #endregion

    #region 右键菜单

    private void OpenContextMenu()
    {
        CloseTransientUiForCapture();
        var menu = new ContextMenu { PlacementTarget = PART_Surface };

        if (_mode == InteractionMode.Annotation)
        {
            AddItem("撤回上一步（Ctrl+Z）", UndoAnnotation);
            AddItem("完成（Enter）", DoneAnnotation);
            AddItem("退出（放弃全部修改）", ExitAnnotation);
        }
        else if (_mode == InteractionMode.Crop)
        {
            AddItem("应用裁剪（Enter）", ApplyCrop);
            AddItem("取消裁剪（Esc）", CancelCrop);
        }
        else
        {
            // 菜单结构对照 Setuna（用户清单）；条目可按设置隐藏
            if (MenuOn("translate"))
                AddItem("翻译(_T)", () => _ = TranslateAsync());
            if (MenuOn("extract"))
                AddItem("提取文字(_E)", () => _ = ExtractTextAsync());
            if (_hasTranslation && MenuOn("toggle_ocr"))
                AddItem(PART_TranslateLayer.Visibility == Visibility.Visible ? "显示原文" : "显示译文",
                    ToggleTranslateOverlay);
            AddSep();
            if (MenuOn("copy"))
                AddItem("复制(_C)", CopyImage);
            if (MenuOn("cut"))
                AddItem("剪切(_X)", CutPin);
            if (MenuOn("paste"))
                AddItem("粘贴(_V)", PasteFromClipboard, enabled: Clipboard.ContainsImage());
            if (MenuOn("saveas"))
                AddItem("另存为...(_S)", SaveAs);
            if (MenuOn("reveal"))
                AddItem("在文件夹中查看(_F)", RevealInFolder);
            AddSep();
            if (MenuOn("zoom50"))
                AddItem("缩放为50%", () => ZoomTo(0.5));
            if (MenuOn("zoom100"))
                AddItem("缩放为100%", () => ZoomTo(1.0));
            if (MenuOn("zoom150"))
                AddItem("缩放为150%", () => ZoomTo(1.5));
            if (MenuOn("zoomout"))
                AddItem("缩小", () => ZoomAtCenter(1 / ZoomStep));
            if (MenuOn("zoomin"))
                AddItem("扩大", () => ZoomAtCenter(ZoomStep));
            if (MenuOn("opacity"))
            {
                AddSub("透明度", new (string, Action)[]
                {
                    ("10%", () => SetOpacity(0.10)),
                    ("25%", () => SetOpacity(0.25)),
                    ("50%", () => SetOpacity(0.50)),
                    ("75%", () => SetOpacity(0.75)),
                    ("100%", () => SetOpacity(1.0)),
                });
            }

            // 边框：无边框 / 单色边框 / 边框颜色 / 窗口阴影
            if (MenuOn("border"))
            {
                var borderMenu = new MenuItem { Header = "边框" };
                var noBorder = new MenuItem { Header = "无边框", IsCheckable = true, IsChecked = _borderStyle == "None" };
                noBorder.Click += (_, _) => SetBorder("None");
                var monoBorder = new MenuItem { Header = "单色边框", IsCheckable = true, IsChecked = _borderStyle != "None" };
                monoBorder.Click += (_, _) => SetBorder("Mono");
                var colorMenu = new MenuItem { Header = "边框颜色" };
                foreach (var (name, hex) in new (string, string)[]
                {
                    ("黑色", "#333333"), ("白色", "#FFFFFF"), ("红色", "#E81123"), ("橙色", "#FF8C00"),
                    ("黄色", "#FFD700"), ("绿色", "#107C10"), ("蓝色", "#0078D7"), ("紫色", "#8764B8"),
                })
                {
                    var colorItem = new MenuItem
                    {
                        Header = name,
                        IsCheckable = true,
                        IsChecked = _borderColor.Equals(hex, StringComparison.OrdinalIgnoreCase),
                    };
                    colorItem.Click += (_, _) => SetBorderColor(hex);
                    colorMenu.Items.Add(colorItem);
                }
                var shadowItem = new MenuItem { Header = "窗口阴影", IsCheckable = true, IsChecked = _shadowEnabled };
                shadowItem.Click += (_, _) => SetShadow(!_shadowEnabled);
                borderMenu.Items.Add(noBorder);
                borderMenu.Items.Add(monoBorder);
                borderMenu.Items.Add(colorMenu);
                borderMenu.Items.Add(shadowItem);
                menu.Items.Add(borderMenu);
            }

            if (MenuOn("imageproc"))
            {
                AddSub("图像处理", new (string, Action)[]
                {
                    ("向右旋转", RotateRight90),
                    ("向左旋转", RotateLeft90),
                    ("垂直翻转", FlipVertical),
                    ("水平翻转", FlipHorizontal),
                });
            }
            AddSep();
            if (MenuOn("annotate"))
            {
                // 单项直进标注工具条，画笔/文字/马赛克在工具条上以图标切换
                AddItem("图片标注", () => EnterAnnotation(AnnotationTool.Pen),
                    enabled: !IsAnimated && _compactSize == null);
            }
            AddSep();
            if (MenuOn("gallery_active"))
                AddItem("参考图名单", () => PinGalleryWindow.ShowActive());
            if (MenuOn("gallery_dust"))
                AddItem("回收站", () => PinGalleryWindow.ShowDustbox());
            if (MenuOn("gallery_clear"))
                AddItem("清空回收站", () => App.Pins.ClearDustbox());
            AddSep();
            if (MenuOn("options"))
                AddItem("选项(_O)", SettingsWindow.ShowSingle);
            if (MenuOn("close"))
                AddItem("关闭(_W)", Close);
            if (MenuOn("destroy"))
                AddItem("销毁(_D)", DestroyToDustbox);
            if (MenuOn("closeall"))
                AddItem("关闭所有贴图(_L)", () => _manager.CloseAll());
        }

        menu.Closed += (_, _) => _activeContextMenu = null;
        _activeContextMenu = menu;
        menu.IsOpen = true;

        void AddItem(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        void AddSep()
        {
            if (menu.Items.Count == 0 || menu.Items[^1] is Separator)
                return;
            menu.Items.Add(new Separator());
        }

        void AddSub(string header, (string, Action)[] entries)
        {
            var parent = new MenuItem { Header = header };
            foreach (var (text, action) in entries)
            {
                var child = new MenuItem { Header = text };
                child.Click += (_, _) => action();
                parent.Items.Add(child);
            }
            menu.Items.Add(parent);
        }
    }

    /// <summary>在文件夹中查看：文件创建的贴图定位源文件，其余打开持久化目录。</summary>
    private void RevealInFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(_sourcePath) && File.Exists(_sourcePath))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_sourcePath}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe",
                    Path.Combine(ConfigStore.DirectoryPath, "pins"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开文件夹失败：{ex.Message}", "Pinshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右键菜单条目是否显示（设置 → 参考图设置 → 贴图菜单项目）。</summary>
    private static bool MenuOn(string key) =>
        !App.Config.MenuVisibility.TryGetValue(key, out var visible) || visible;

    /// <summary>销毁：关闭并放入回收站（可从回收站还原）。</summary>
    private void DestroyToDustbox()
    {
        _toDustbox = true;
        Close();
    }

    /// <summary>自检用：打开标注工具条。</summary>
    internal void OpenToolbarForTest()
    {
        EnterAnnotation(AnnotationTool.Pen);
    }

    /// <summary>提取文字（供截图工具条与右键调用）。</summary>
    internal void ExtractText() => _ = ExtractTextAsync();

    private async Task ExtractTextAsync()
    {
        try
        {
            var result = await OcrService.RecognizeAsync(_bitmap, App.Config.OcrLanguage);
            var text = string.Join(Environment.NewLine,
                result.Lines.Select(l => l.Text.Trim()).Where(line => line.Length > 0));
            if (string.IsNullOrWhiteSpace(text))
                return;
            ExtractPanelWindow.Show(this, text);
        }
        catch (Exception ex)
        {
            ShowResult("文字提取失败", ex.Message);
        }
    }

    private bool _hasTranslation;
    private bool _isTranslating;

    /// <summary>图上原位翻译（微信式）：识别文字块，译文按原位置覆盖显示。</summary>
    internal async Task TranslateAsync()
    {
        if (_isClosing || _isTranslating)
            return; // 正在翻译：忽略重复触发
        _isTranslating = true;
        ShowTranslateLoading(true);
        try
        {
            var ocr = await OcrService.RecognizeAsync(_bitmap, App.Config.OcrLanguage);
            var lines = ocr.Lines.Take(200).ToList();
            if (lines.Count == 0)
                return; // 无文字：静默返回

            // 逐行翻译：行序与图片原始分行完全一致（对照窗口与图上覆盖都按行对齐）
            var translated = new string[lines.Count];
            var failed = new bool[lines.Count];
            var semaphore = new SemaphoreSlim(4);
            var tasks = new Task[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                var index = i;
                tasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        translated[index] = await TranslateService.TranslateAsync(lines[index].Text, App.Config);
                    }
                    catch
                    {
                        // 单行失败保留原文，不打断整图翻译
                        failed[index] = true;
                        translated[index] = lines[index].Text;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
            }
            await Task.WhenAll(tasks);

            // 失败行补译一轮：串行 + 间隔，躲开免费接口的突发频控
            // （第一轮并发 4 冲过去的行不再动；仍失败的行保留原文）
            var retried = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!failed[i] || ++retried > 60)
                    continue;
                try
                {
                    await Task.Delay(200);
                    translated[i] = await TranslateService.TranslateAsync(lines[i].Text, App.Config);
                    failed[i] = false;
                }
                catch
                {
                    // 仍失败：保留原文
                }
            }

            // 图片翻译开关：勾选时逐行覆盖译文（失败行保留原文）
            if (App.Config.TranslateOverlay)
                BuildTranslateOverlay(lines, translated, failed);
            else
                PART_TranslateLayer.Visibility = Visibility.Collapsed;

            // 对照翻译开关：弹出对照窗口（原文/译文逐行一一对应）
            if (App.Config.TranslateShowCompareWindow)
            {
                var sourceText = string.Join(Environment.NewLine, lines.Select(l => l.Text));
                var translatedText = string.Join(Environment.NewLine,
                    lines.Select((_, i) => failed[i] ? lines[i].Text : translated[i]));
                TranslateWindow.Show(this, sourceText, translatedText);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("TranslateAsync", ex); // 带堆栈落盘，便于定位“翻译失败”
            ShowResult("翻译失败", ex.Message);
        }
        finally
        {
            _isTranslating = false;
            ShowTranslateLoading(false);
        }
    }

    /// <summary>
    /// 翻译中 loading：贴图上盖半透明遮罩 + 旋转圆弧，让等待可感知。
    /// 遮罩不参与命中测试，拖拽/右键菜单不受影响；小贴图按比例缩小 spinner。
    /// </summary>
    internal void ShowTranslateLoading(bool show)
    {
        if (show)
        {
            var minDim = Math.Min(PART_Surface.ActualWidth, PART_Surface.ActualHeight);
            var scale = Math.Clamp(minDim / 90.0, 0.4, 1.0);
            PART_SpinnerScale.ScaleX = PART_SpinnerScale.ScaleY = scale;
            PART_LoadingOverlay.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            PART_SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            PART_LoadingOverlay.Visibility = Visibility.Collapsed;
            PART_SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    /// <summary>
    /// 逐行原位覆盖译文：每行 OCR 结果各自覆盖在原行位置。
    /// 坐标/字号用位图像素，配合 PART_TranslateLayer 的 LayoutTransform 随贴图缩放同步缩放。
    /// 字号以本行行高适配（大行封顶到中位数的 1.2 倍），覆盖块尺寸锁定原行框——
    /// 译文变长只在框内省略号截断，不会向下/向右生长压到相邻行（旧版文字叠加的原因）。
    /// </summary>
    private void BuildTranslateOverlay(IReadOnlyList<OcrLine> lines, string[] translations, bool[] failed)
    {
        PART_TranslateLayer.Children.Clear();
        var heights = lines.Select(l => (double)l.LineHeight).OrderBy(h => h).ToList();
        var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 0d;
        for (var i = 0; i < lines.Count; i++)
        {
            if (failed != null && i < failed.Length && failed[i])
                continue; // 失败行保留原文（不遮不报）
            var lineRect = lines[i].Rect;
            if (string.IsNullOrWhiteSpace(translations[i]))
                continue;
            if (lineRect.Width < 4 || lineRect.Height < 4)
                continue;

            var fontPx = Math.Clamp(
                Math.Min(lineRect.Height * 0.85, medianHeight * 1.2), 9, 200);

            var background = SampleBackground(lineRect);
            var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            var foreground = luminance > 0.55 ? Brushes.Black : Brushes.White;
            var backgroundBrush = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B));
            backgroundBrush.Freeze();

            var text = new TextBlock
            {
                Text = translations[i],
                FontSize = fontPx,
                Foreground = foreground,
                TextWrapping = TextWrapping.NoWrap,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Microsoft YaHei"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(fontPx * 0.12, 0, fontPx * 0.12, 0),
            };

            // 尺寸锁定原行框（Width 略放宽半个字容纳边距），杜绝块间叠加
            var host = new Border
            {
                Width = lineRect.Width + fontPx * 0.5,
                Height = lineRect.Height,
                Background = backgroundBrush,
                CornerRadius = new CornerRadius(1),
                Child = text,
            };
            Canvas.SetLeft(host, lineRect.Left);
            Canvas.SetTop(host, lineRect.Top);
            PART_TranslateLayer.Children.Add(host);
        }

        PART_TranslateLayer.Visibility = Visibility.Visible;
        _hasTranslation = true;
        UpdateTranslateLayerTransform();
    }

    /// <summary>译文覆盖层按贴图当前显示尺寸整体缩放（位图像素坐标 → 屏幕 DIP），缩放贴图即跟手。</summary>
    private void UpdateTranslateLayerTransform()
    {
        if (_bitmap == null || _bitmap.Width < 1 || PART_Surface.Width <= 0)
            return;
        var factor = PART_Surface.Width / _bitmap.Width;
        PART_TranslateLayer.LayoutTransform = new ScaleTransform(factor, factor);
    }

    /// <summary>取样文字块周边背景色（取边框上的点平均）。</summary>
    private System.Drawing.Color SampleBackground(System.Drawing.RectangleF rect)
    {
        int r = 0, g = 0, b = 0, count = 0;
        var inflate = Math.Max(2f, rect.Height * 0.25f);
        var outer = System.Drawing.RectangleF.Inflate(rect, inflate, inflate);
        var points = new[]
        {
            new System.Drawing.PointF(outer.Left, outer.Top),
            new System.Drawing.PointF(outer.Right, outer.Top),
            new System.Drawing.PointF(outer.Left, outer.Bottom),
            new System.Drawing.PointF(outer.Right, outer.Bottom),
            new System.Drawing.PointF((outer.Left + outer.Right) / 2, outer.Top),
            new System.Drawing.PointF((outer.Left + outer.Right) / 2, outer.Bottom),
        };
        foreach (var point in points)
        {
            var x = Math.Clamp((int)point.X, 0, _bitmap.Width - 1);
            var y = Math.Clamp((int)point.Y, 0, _bitmap.Height - 1);
            var color = _bitmap.GetPixel(x, y);
            r += color.R; g += color.G; b += color.B; count++;
        }
        return System.Drawing.Color.FromArgb(r / count, g / count, b / count);
    }

    private void ToggleTranslateOverlay()
    {
        if (!_hasTranslation)
            return;
        PART_TranslateLayer.Visibility = PART_TranslateLayer.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>位图被编辑（旋转/裁剪/标注）后，译文覆盖层作废。</summary>
    private void ClearTranslation()
    {
        PART_TranslateLayer.Children.Clear();
        PART_TranslateLayer.Visibility = Visibility.Collapsed;
        _hasTranslation = false;
    }

    private void ShowResult(string title, string text) => ResultWindow.Show(this, title, text);

    private void CopyImage() => Clipboard.SetImage(_displaySource);

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = IsAnimated ? "GIF 动画|*.gif|PNG|*.png" : "PNG|*.png|JPEG|*.jpg|BMP|*.bmp",
            FileName = $"pin_{DateTime.Now:yyyyMMdd_HHmmss}{(IsAnimated ? ".gif" : ".png")}"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            if (IsAnimated && dialog.FilterIndex == 1)
                File.WriteAllBytes(dialog.FileName, _gifBytes!);
            else
            {
                var format = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => System.Drawing.Imaging.ImageFormat.Jpeg,
                    ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                    ".gif" => System.Drawing.Imaging.ImageFormat.Gif,
                    _ => System.Drawing.Imaging.ImageFormat.Png,
                };
                _bitmap.Save(dialog.FileName, format);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "Pinshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region 持久化接口

    /// <summary>恢复持久化状态（重启恢复用）。</summary>
    internal void ApplyPersisted(double scale, double opacity, bool compact,
        string? borderStyle = null, string? borderColor = null, bool shadow = true)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        SetOpacity(opacity);
        if (!string.IsNullOrEmpty(borderStyle))
            _borderStyle = borderStyle;
        if (!string.IsNullOrEmpty(borderColor))
            _borderColor = borderColor;
        _shadowEnabled = shadow;
        ApplyBorder();
        if (compact)
        {
            _preCompactScale = _scale;
            var dpi = GetDpi();
            _compactSize = new DrawingRectangle(0, 0,
                Math.Max(1, (int)Math.Ceiling(CompactSizeDip * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(CompactSizeDip * dpi.DpiScaleY)));
            PART_Frame.Visibility = Visibility.Visible;
        }
        ApplyBounds();
    }

    internal PinPersistRecord GetPersistRecord(string file) => new(
        file,
        _baseBounds.Left,
        _baseBounds.Top,
        _scale,
        _userOpacity,
        _compactSize != null,
        _borderStyle,
        _borderColor,
        _shadowEnabled);

    /// <summary>把贴图当前图像写入目录，返回是否成功与实际文件名。</summary>
    internal bool WriteImage(string directory, string baseName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? writtenFile)
    {
        try
        {
            if (IsAnimated)
            {
                writtenFile = baseName + ".gif";
                File.WriteAllBytes(Path.Combine(directory, writtenFile), _gifBytes!);
            }
            else
            {
                writtenFile = baseName + ".png";
                _bitmap.Save(Path.Combine(directory, writtenFile), System.Drawing.Imaging.ImageFormat.Png);
            }
            return true;
        }
        catch
        {
            writtenFile = null;
            return false;
        }
    }

    /// <summary>托盘批量另存为：按扩展名保存到指定路径。</summary>
    internal void SaveToFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch
        {
            ".jpg" or ".jpeg" => System.Drawing.Imaging.ImageFormat.Jpeg,
            ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
            ".gif" => System.Drawing.Imaging.ImageFormat.Gif,
            _ => System.Drawing.Imaging.ImageFormat.Png,
        };
        _bitmap.Save(path, format);
    }

    /// <summary>把贴图当前图像序列化为字节（回收站用）。</summary>
    internal bool SaveImageBytes(out byte[] data, out bool isGif)
    {
        try
        {
            if (IsAnimated && _gifBytes != null)
            {
                data = _gifBytes;
                isGif = true;
                return true;
            }
            using var stream = new MemoryStream();
            _bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            data = stream.ToArray();
            isGif = false;
            return true;
        }
        catch
        {
            data = [];
            isGif = false;
            return false;
        }
    }

    #endregion

    #region 布局与窗口管理

    private DpiScale GetDpi() => _sourceInitialized ? VisualTreeHelper.GetDpi(this) :
        Win32Helper.GetDpiScaleForPhysicalPoint(_baseBounds.Left + _baseBounds.Width / 2,
            _baseBounds.Top + _baseBounds.Height / 2);

    private void ApplyBounds()
    {
        if (_isClosing)
            return;
        var content = ContentBounds;
        var dpi = GetDpi();
        var windowBounds = CalculateWindowBounds(content, dpi);
        Left = windowBounds.Left / dpi.DpiScaleX;
        Top = windowBounds.Top / dpi.DpiScaleY;
        Width = windowBounds.Width / dpi.DpiScaleX;
        Height = windowBounds.Height / dpi.DpiScaleY;
        PART_Surface.Width = content.Width / dpi.DpiScaleX;
        PART_Surface.Height = content.Height / dpi.DpiScaleY;
        PART_Surface.Margin = new Thickness(
            (content.Left - windowBounds.Left) / dpi.DpiScaleX,
            (content.Top - windowBounds.Top) / dpi.DpiScaleY,
            0, 0);
        UpdateTranslateLayerTransform(); // 缩放/DPI 变化后译文覆盖层跟随贴图尺寸
        if (!_sourceInitialized)
            return;
        SetPhysicalWindowBounds(dpi);
    }

    private void SetPhysicalWindowBounds(DpiScale dpi)
    {
        var bounds = CalculateWindowBounds(ContentBounds, dpi);
        Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    internal static DrawingRectangle CalculateWindowBounds(DrawingRectangle contentBounds, DpiScale dpi)
    {
        var marginX = Math.Max(1, (int)Math.Ceiling(ShadowMarginDip * dpi.DpiScaleX));
        var marginY = Math.Max(1, (int)Math.Ceiling(ShadowMarginDip * dpi.DpiScaleY));
        return DrawingRectangle.FromLTRB(
            contentBounds.Left - marginX,
            contentBounds.Top - marginY,
            contentBounds.Right + marginX,
            contentBounds.Bottom + marginY);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x02E0) // WM_DPICHANGED：按物理尺寸重排
            Dispatcher.BeginInvoke(ApplyBounds, DispatcherPriority.Loaded);
        else if (msg == 0x007E) // WM_DISPLAYCHANGE：拔掉显示器后移回最近工作区
            Dispatcher.BeginInvoke(RestoreToVisibleMonitor, DispatcherPriority.Loaded);
        return 0;
    }

    private void RestoreToVisibleMonitor()
    {
        if (_isClosing)
            return;
        var content = ContentBounds;
        var bounds = new Rect(content.X, content.Y, content.Width, content.Height);
        if (Win32.GetMonitorWorkAreas().Any(area => area.IntersectsWith(bounds)))
            return;
        var workArea = Win32Helper.GetNearestMonitorWorkArea(this);
        var current = ContentBounds;
        _baseBounds = new DrawingRectangle(
            (int)Math.Clamp(_baseBounds.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - current.Width)),
            (int)Math.Clamp(_baseBounds.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - current.Height)),
            _baseBounds.Width, _baseBounds.Height);
        ApplyBounds();
    }

    private static BitmapSource ToBitmapSource(DrawingBitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (hBitmap != nint.Zero)
                Win32.DeleteObject(hBitmap);
        }
    }

    #endregion

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        CloseTransientUiForCapture();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _manager.Unregister(this, _toDustbox);
        _manager.NotifyStateChanged();
        _hwndSource?.RemoveHook(WndProc);
        _bitmap.Dispose();
        base.OnClosed(e);
    }
}
