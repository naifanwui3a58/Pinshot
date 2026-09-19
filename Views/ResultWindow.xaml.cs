using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>OCR/翻译结果浮窗：出现在贴图右侧（不够则左侧），Esc 关闭，可复制文本。</summary>
public partial class ResultWindow : Window
{
    private ResultWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    /// <summary>显示结果窗口，定位在贴图窗口旁边。</summary>
    internal static void Show(Window owner, string title, string text)
    {
        var window = new ResultWindow
        {
            Owner = owner,
            Title = title,
        };
        window.PART_Title.Text = title;
        window.PART_Text.Text = text;
        window.Loaded += (_, _) => window.PlaceNear(owner);
        window.Show();
        window.PART_Text.Focus();
    }

    private void PlaceNear(Window owner)
    {
        // 优先放在贴图右侧，屏幕放不下放左侧，再不行放贴图下方
        var ownerRight = owner.Left + owner.Width;
        var x = ownerRight + 8;
        var y = owner.Top;
        if (x + ActualWidth > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
            x = Math.Max(SystemParameters.VirtualScreenLeft, owner.Left - ActualWidth - 8);
        if (y + ActualHeight > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            y = Math.Max(SystemParameters.VirtualScreenTop, owner.Top + owner.Height - ActualHeight);
        Left = x;
        Top = y;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PART_Text.Text);
            PART_CopyButton.Content = "已复制";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.2)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                PART_CopyButton.Content = "复制";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
