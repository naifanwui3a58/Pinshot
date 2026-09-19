using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>
/// 中英对照翻译窗口 = 提取文字窗口 + 底部译文区：
/// 上半部分与提取文字窗口完全同款（原文即提取结果），分隔线下方是译文。
/// 高度自适应：文本框收缩到内容实际高度（SizeToContent 让窗口严丝合缝），
/// 原文/译文合计超过屏幕时按内容比例分高度并各自滚动。
/// </summary>
public partial class TranslateWindow : Window
{
    private readonly PinWindow _owner;
    private readonly string _translated;

    private TranslateWindow(PinWindow owner, string source, string translated)
    {
        _owner = owner;
        _translated = translated;
        InitializeComponent();
        ApplyVisibility();
        PART_SourceText.Text = source;
        PART_TranslatedText.Text = translated;
        UpdateTranslatedLabel();
        Loaded += (_, _) =>
        {
            FitSize();
            PlaceNearOwner();
        };
        PART_SourceText.TextChanged += OnTextChanged;
        PART_TranslatedText.TextChanged += OnTextChanged;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>按设置显隐原文/译文区（都关时按都开处理，避免出现只有标题的空窗口）。</summary>
    private void ApplyVisibility()
    {
        var showSource = App.Config.CompareShowSource;
        var showTranslation = App.Config.CompareShowTranslation;
        if (!showSource && !showTranslation)
            showSource = showTranslation = true;
        PART_SourceText.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        PART_TranslatedSection.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        PART_CopyButton.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>显示对照窗口（同贴图只开一个，内容随更新刷新）。</summary>
    internal static void Show(PinWindow owner, string source, string translated)
    {
        var existing = Application.Current.Windows.OfType<TranslateWindow>()
            .FirstOrDefault(w => ReferenceEquals(w._owner, owner));
        if (existing != null)
        {
            existing.ApplyVisibility();
            existing.PART_SourceText.Text = source;
            existing.PART_TranslatedText.Text = translated;
            existing.UpdateTranslatedLabel();
            existing.Activate();
            return;
        }
        new TranslateWindow(owner, source, translated) { Owner = owner }.Show();
    }

    /// <summary>
    /// 高度自适应（提取文字窗口同款量法）：可见的文本框收缩到内容实际高度；
    /// 原文/译文合计放不下时按内容比例分配高度、各自出滚动条。宽度固定与提取窗口一致。
    /// </summary>
    private void FitSize()
    {
        var showSource = PART_SourceText.Visibility == Visibility.Visible;
        var showTranslation = PART_TranslatedSection.Visibility == Visibility.Visible;
        PART_SourceText.Height = double.NaN;
        PART_TranslatedText.Height = double.NaN;
        UpdateLayout();
        var sH = showSource
            ? PART_SourceText.ExtentHeight + PART_SourceText.Padding.Top + PART_SourceText.Padding.Bottom + 12
            : 0;
        var tH = showTranslation
            ? PART_TranslatedText.ExtentHeight + PART_TranslatedText.Padding.Top + PART_TranslatedText.Padding.Bottom + 12
            : 0;
        // 预算：提取窗口单框预算（工作区-120）为基准，再扣除标题行/分隔行/按钮行，两框共享
        var availH = Math.Max(200, SystemParameters.WorkArea.Height - 220);

        if (sH + tH <= availH)
        {
            if (showSource)
            {
                PART_SourceText.Height = sH;
                PART_SourceText.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }
            if (showTranslation)
            {
                PART_TranslatedText.Height = tH;
                PART_TranslatedText.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }
        }
        else
        {
            var scale = availH / (sH + tH);
            if (showSource)
            {
                PART_SourceText.Height = Math.Max(80, sH * scale);
                PART_SourceText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            if (showTranslation)
            {
                PART_TranslatedText.Height = Math.Max(80, tH * scale);
                PART_TranslatedText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
        }
        UpdateLayout();
        PlaceNearOwner();
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => FitSize();

    /// <summary>译文与原文一致（命令/代码/专有名词，或原文即目标语言）时在标签上说明，避免“看起来没翻”。</summary>
    private void UpdateTranslatedLabel()
    {
        var identical = string.Equals(
            PART_TranslatedText.Text.Trim(),
            PART_SourceText.Text.Trim(),
            StringComparison.Ordinal);
        PART_TranslatedLabel.Text = identical
            ? "译文（与原文一致：本段没有可翻译的文字内容）"
            : "译文";
    }

    /// <summary>显示对照窗口（同贴图只开一个，内容随更新刷新）。</summary>
    private void PlaceNearOwner()
    {
        var x = _owner.Left + _owner.Width + 10;
        if (x + ActualWidth > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
            x = Math.Max(SystemParameters.VirtualScreenLeft, _owner.Left - ActualWidth - 10);
        var y = _owner.Top;
        if (y + ActualHeight > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            y = Math.Max(SystemParameters.VirtualScreenTop, _owner.Top + _owner.Height - ActualHeight);
        Left = x;
        Top = y;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed &&
            e.OriginalSource is not System.Windows.Controls.Button)
            DragMove();
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Clipboard.SetText(_translated);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
