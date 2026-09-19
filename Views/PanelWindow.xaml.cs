using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>启动主面板（Setuna 式：截取 / 翻译 / 选项），无系统标题栏。</summary>
public partial class PanelWindow : Window
{
    private static PanelWindow? _instance;

    private PanelWindow()
    {
        InitializeComponent();
        PART_CaptureButton.Click += (_, _) => _ = App.CapturePinAsync(hidePanelDuringCapture: true);
        PART_OptionButton.Click += (_, _) => SettingsWindow.ShowSingle();
        PART_HideButton.Click += (_, _) => Hide();
        Closing += OnClosing;
    }

    /// <summary>无标题栏窗口：按住空白处可拖动。</summary>
    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;
        if (e.OriginalSource is System.Windows.Controls.Button)
            return;
        DragMove();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 点关闭视为收到托盘，程序继续驻留
        e.Cancel = true;
        Hide();
    }

    internal static void ShowSingle()
    {
        if (_instance == null)
        {
            _instance = new PanelWindow();
            _instance.Closed += (_, _) => _instance = null;
        }
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>当前可见的面板实例（截图时需要临时隐藏，没有则返回 null）。</summary>
    internal static PanelWindow? VisibleInstance =>
        _instance is { IsVisible: true } ? _instance : null;
}
