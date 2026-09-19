using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pinshot.Core;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Pinshot.Views;

/// <summary>
/// 截图覆盖层（SETUNA 式）：冻结画面全屏覆盖，悬停自动吸附窗口/控件（UIA + 顶层窗口两级），
/// 单击选中窗口、拖拽自定义选区，松手即完成出贴图——没有确认环节。
/// 可选全屏十字光标与放大镜。多显示器时每屏一个实例，共享选区状态。
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private static readonly List<CaptureOverlayWindow> Instances = [];
    private static TaskCompletionSource<DrawingRectangle?>? _completion;
    private static bool _completed;

    private readonly DrawingRectangle _monitorBounds;
    private readonly Bitmap _frozen;
    private readonly List<WindowHit> _windows;
    private readonly DpiScale _dpi;
    private readonly bool _crosshair;
    private readonly bool _magnifier;
    private readonly BitmapSource _frozenSource;

    private DrawingRectangle? _hoverRect;
    private DrawingRectangle? _selection;
    private bool _dragging;
    private System.Drawing.Point _dragOrigin;

    internal CaptureOverlayWindow(DrawingRectangle monitorBounds, Bitmap frozen, List<WindowHit> windows,
        DpiScale dpi, bool crosshair, bool magnifier)
    {
        _monitorBounds = monitorBounds;
        _frozen = frozen;
        _windows = windows;
        _dpi = dpi;
        _crosshair = crosshair;
        _magnifier = magnifier;

        InitializeComponent();
        Title = "Pinshot 截图";

        _frozenSource = CreateFrozenSource(frozen);
        PART_Backdrop.Source = _frozenSource;
        PART_Backdrop.Width = frozen.Width / _dpi.DpiScaleX;
        PART_Backdrop.Height = frozen.Height / _dpi.DpiScaleY;

        Left = monitorBounds.Left / _dpi.DpiScaleX;
        Top = monitorBounds.Top / _dpi.DpiScaleY;
        Width = monitorBounds.Width / _dpi.DpiScaleX;
        Height = monitorBounds.Height / _dpi.DpiScaleY;

        PART_CrossH.Visibility = crosshair ? Visibility.Visible : Visibility.Collapsed;
        PART_CrossV.Visibility = crosshair ? Visibility.Visible : Visibility.Collapsed;
        PART_Magnifier.Visibility = magnifier ? Visibility.Visible : Visibility.Collapsed;

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
        Closed += (_, _) => timer.Stop();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Complete(null);
        };
        KeyDown += OnKeyDown;
    }

    #region 静态协作（多显示器）

    internal static void Reset()
    {
        // 续体异步执行：避免在窗口关闭过程中同步重入 CaptureService
        _completion = new TaskCompletionSource<DrawingRectangle?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _completed = false;
    }

    internal static Task<DrawingRectangle?> WaitSelectionAsync() =>
        (_completion ??= new TaskCompletionSource<DrawingRectangle?>(
            TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal static void CloseAll()
    {
        foreach (var window in Instances.ToArray())
            window.Close();
        Instances.Clear();
    }

    private static void Complete(DrawingRectangle? rect)
    {
        if (_completed)
            return;
        _completed = true;
        _completion?.TrySetResult(rect);
    }

    /// <summary>自检用：等同于按 Esc 取消。</summary>
    internal static void CancelForSelfTest() => Complete(null);

    /// <summary>自检用：等同于在指定物理区域框选完成。</summary>
    internal static void SelectForSelfTest(DrawingRectangle rect) => Complete(rect);

    /// <summary>自检用：描述悬停吸附状态。</summary>
    internal static string DescribeHoverForSelfTest()
    {
        if (Instances.Count == 0)
            return "无覆盖层实例";
        var instance = Instances[0];
        return $"覆盖层 {Instances.Count} 个；候选窗口 {instance._windows.Count} 个；" +
               $"当前悬停={(instance._hoverRect == null ? "无" : instance._hoverRect.ToString())}";
    }

    #endregion

    private static BitmapSource CreateFrozenSource(Bitmap frozen)
    {
        var hBitmap = frozen.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Win32.DeleteObject(hBitmap);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32Helper.HideFromAltTab(this);
        Instances.Add(this);
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        Instances.Remove(this);
        if (Instances.Count == 0 && !_completed)
            Complete(null);
        base.OnClosed(e);
    }

    private bool TryGetLocalCursor(out System.Windows.Point localDip, out System.Drawing.Point global)
    {
        if (!Win32Helper.TryGetCursorPosition(out global))
        {
            localDip = default;
            return false;
        }
        localDip = new System.Windows.Point(
            (global.X - _monitorBounds.Left) / _dpi.DpiScaleX,
            (global.Y - _monitorBounds.Top) / _dpi.DpiScaleY);
        return true;
    }

    private bool ContainsCursor(DrawingRectangle bounds, System.Drawing.Point cursor) =>
        cursor.X >= bounds.Left && cursor.X < bounds.Right &&
        cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;

    private Rect ToDip(DrawingRectangle rect) => new(
        (rect.Left - _monitorBounds.Left) / _dpi.DpiScaleX,
        (rect.Top - _monitorBounds.Top) / _dpi.DpiScaleY,
        rect.Width / _dpi.DpiScaleX,
        rect.Height / _dpi.DpiScaleY);

    private DrawingRectangle ClampToMonitor(DrawingRectangle rect) =>
        DrawingRectangle.Intersect(rect, _monitorBounds);

    private void OnTick()
    {
        if (_completed || !TryGetLocalCursor(out var local, out var global))
            return;
        if (!ContainsCursor(_monitorBounds, global))
            return;

        // 空闲态：悬停吸附（UIA 元素 + 顶层窗口两级 + 像素边界兜底）
        if (!_dragging && _selection == null)
        {
            _hoverRect = null;

            // 先确定包含光标的顶层窗口（给 UIA 提供激活目标）
            DrawingRectangle? windowRect = null;
            nint windowHandle = 0;
            foreach (var hit in _windows)
            {
                if (ContainsCursor(hit.Bounds, global) && hit.Bounds.IntersectsWith(_monitorBounds))
                {
                    windowRect = hit.Bounds;
                    windowHandle = hit.Handle;
                    break;
                }
            }

            var elementRect = UiaProbe.GetElementRect(global, windowHandle);
            if (!elementRect.IsEmpty && ContainsCursor(elementRect, global) &&
                elementRect.IntersectsWith(_monitorBounds))
                _hoverRect = elementRect;

            if (_hoverRect != null && windowRect is { } win)
            {
                var clamped = DrawingRectangle.Intersect(_hoverRect.Value, win);
                _hoverRect = clamped.IsEmpty ? windowRect : clamped;
            }
            else if (_hoverRect == null)
            {
                _hoverRect = windowRect;

                // 第三级兜底：像素梯度边界（自绘程序/桌面，UIA 与窗口都拿不到时）
                if (_hoverRect == null)
                {
                    var pixelRect = PixelEdgeProbe.DetectRegion(_frozen, _monitorBounds, global);
                    if (!pixelRect.IsEmpty && pixelRect.IntersectsWith(_monitorBounds) &&
                        (long)pixelRect.Width * pixelRect.Height < (long)_monitorBounds.Width * _monitorBounds.Height / 4)
                        _hoverRect = pixelRect;
                }
            }
        }

        UpdateVisuals(local, global);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Win32Helper.TryGetCursorPosition(out var cursor))
            return;
        _dragOrigin = cursor;
        _selection = null;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !Win32Helper.TryGetCursorPosition(out var cursor))
            return;
        _selection = ClampToMonitor(DrawingRectangle.FromLTRB(
            Math.Min(_dragOrigin.X, cursor.X), Math.Min(_dragOrigin.Y, cursor.Y),
            Math.Max(_dragOrigin.X, cursor.X), Math.Max(_dragOrigin.Y, cursor.Y)));
        // 起手几乎没移动 → 视为单击，吸附到悬停窗口
        if (_selection.Value.Width < 6 && _selection.Value.Height < 6 && _hoverRect is { } hover)
            _selection = ClampToMonitor(hover);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();

        // 松手即完成出贴图：
        // 拖拽了有效区域 → 该区域；几乎没动 → 悬停窗口；悬停也没有（空白桌面）→ 整个显示器
        if (_selection is { } sel && sel.Width >= 6 && sel.Height >= 6)
            Complete(ClampToMonitor(sel));
        else if (_hoverRect is { } hover)
            Complete(ClampToMonitor(hover));
        else
            Complete(_monitorBounds);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
        else if (e.Key == Key.Enter && _hoverRect is { } hover)
        {
            e.Handled = true;
            Complete(ClampToMonitor(hover));
        }
    }

    private void UpdateVisuals(System.Windows.Point localDip, System.Drawing.Point global)
    {
        var current = _selection ?? _hoverRect;
        var dipRect = current is { } rect ? ToDip(rect) : new Rect(0, 0, 0, 0);

        if (current is { } shown)
        {
            PART_Highlight.Visibility = Visibility.Visible;
            Canvas.SetLeft(PART_Highlight, dipRect.Left);
            Canvas.SetTop(PART_Highlight, dipRect.Top);
            PART_Highlight.Width = dipRect.Width;
            PART_Highlight.Height = dipRect.Height;
        }
        else
        {
            PART_Highlight.Visibility = Visibility.Collapsed;
        }

        // 选区外遮罩
        SetRect(PART_DimTop, 0, 0, ActualWidth, dipRect.Top);
        SetRect(PART_DimBottom, 0, dipRect.Bottom, ActualWidth, ActualHeight - dipRect.Bottom);
        SetRect(PART_DimLeft, 0, dipRect.Top, dipRect.Left, dipRect.Height);
        SetRect(PART_DimRight, dipRect.Right, dipRect.Top, ActualWidth - dipRect.Right, dipRect.Height);

        // 十字线在拖拽中隐藏（避免干扰）
        var showCross = _crosshair && !_dragging;
        PART_CrossH.Visibility = showCross ? Visibility.Visible : Visibility.Collapsed;
        PART_CrossV.Visibility = showCross ? Visibility.Visible : Visibility.Collapsed;
        if (showCross)
        {
            PART_CrossH.X1 = 0;
            PART_CrossH.X2 = ActualWidth;
            PART_CrossH.Y1 = PART_CrossH.Y2 = localDip.Y;
            PART_CrossV.Y1 = 0;
            PART_CrossV.Y2 = ActualHeight;
            PART_CrossV.X1 = PART_CrossV.X2 = localDip.X;
        }

        UpdateMagnifier(localDip, global, current);
    }

    private void UpdateMagnifier(System.Windows.Point localDip, System.Drawing.Point global, DrawingRectangle? current)
    {
        if (!_magnifier)
            return;
        const double offset = 18;
        var x = localDip.X + offset;
        var y = localDip.Y + offset;
        if (x + PART_Magnifier.Width > ActualWidth)
            x = localDip.X - PART_Magnifier.Width - offset;
        if (y + PART_Magnifier.Height > ActualHeight)
            y = localDip.Y - PART_Magnifier.Height - offset;
        PART_Magnifier.Margin = new Thickness(x, y, 0, 0);

        const int sample = 32;
        var maxX = Math.Max(0, _frozen.Width - sample);
        var maxY = Math.Max(0, _frozen.Height - sample);
        var sourceX = Math.Clamp(global.X - _monitorBounds.Left - sample / 2, 0, maxX);
        var sourceY = Math.Clamp(global.Y - _monitorBounds.Top - sample / 2, 0, maxY);
        var width = Math.Min(sample, _frozen.Width);
        var height = Math.Min(sample, _frozen.Height);
        if (width > 0 && height > 0)
        {
            try
            {
                PART_MagnifierImage.Source = new CroppedBitmap(
                    _frozenSource, new Int32Rect(sourceX, sourceY, width, height));
            }
            catch
            {
                // 边界裁切失败忽略本帧
            }
        }
        PART_MagnifierText.Text = current is { } rect ? $"{rect.Width} × {rect.Height}" : string.Empty;
    }

    private static void SetRect(System.Windows.Shapes.Rectangle element, double x, double y, double width, double height)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        element.Margin = new Thickness(x, y, 0, 0);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
