using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>提取文字窗口：一次性显示识别到的全部文字，可全选复制。</summary>
public partial class ExtractPanelWindow : Window
{
    private readonly PinWindow _owner;
    private readonly string _text;

    private ExtractPanelWindow(PinWindow owner, string text)
    {
        _owner = owner;
        _text = text;
        InitializeComponent();
        PART_Text.Text = text;
        Loaded += (_, _) =>
        {
            FitHeight();
            PlaceNearOwner();
            PART_Text.Focus();
        };
        PART_Text.TextChanged += (_, _) => FitHeight();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    internal static void Show(PinWindow owner, string text)
    {
        var existing = Application.Current.Windows.OfType<ExtractPanelWindow>()
            .FirstOrDefault(w => ReferenceEquals(w._owner, owner));
        if (existing != null)
        {
            existing.PART_Text.Text = text;
            existing.Activate();
            return;
        }
        new ExtractPanelWindow(owner, text) { Owner = owner }.Show();
    }

    /// <summary>TextBox 高度收缩到内容实际高度（SizeToContent 据此让窗口贴合，无空白）。</summary>
    private void FitHeight()
    {
        PART_Text.Height = double.NaN;
        PART_Text.UpdateLayout();
        // 高度=内容高度；仅当内容放不下时滚动（上限=屏幕工作区减去顶栏/任务栏余量）
        var maxTextHeight = SystemParameters.WorkArea.Height - 120;
        PART_Text.MaxHeight = Math.Max(200, maxTextHeight);
        PART_Text.Height = Math.Min(
            PART_Text.ExtentHeight + PART_Text.Padding.Top + PART_Text.Padding.Bottom + 12,
            PART_Text.MaxHeight);
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => FitHeight();

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

    private void OnCopyAll(object sender, RoutedEventArgs e) => Clipboard.SetText(_text);

    private void OnTranslate(object sender, RoutedEventArgs e) => _ = _owner.TranslateAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
