using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pinshot.Views;

/// <summary>
/// 快捷键感应框：点击进入录入态（蓝框+提示），按下的组合键或单键即录入。
/// 不限制修饰键——数字 1、字母 A、F5、Space、组合键都可以作为全局热键。
/// Backspace / Delete 清空，Esc 取消并还原。
/// 存储格式："Mod1+Mod2+Key"（如 Alt+Shift+P、D1、F5、Space）。
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0xCD, 0xD3, 0xDB));
    private static readonly SolidColorBrush RecordingBrush = new(Color.FromRgb(0x2D, 0x7D, 0xFA));
    private static readonly SolidColorBrush HintBrush = new(Color.FromRgb(0xA8, 0xAE, 0xB8));
    private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0x1D, 0x21, 0x29));

    private string _value = "";
    private string _beforeEdit = "";
    private bool _recording;

    public HotkeyBox()
    {
        IsReadOnly = true;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) => StartRecording();
        LostKeyboardFocus += (_, _) => StopRecording();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, e) =>
        {
            if (!IsKeyboardFocused)
            {
                Focus();
                e.Handled = true;
            }
        };
    }

    /// <summary>当前热键字符串，空表示未设置。</summary>
    public string HotkeyValue
    {
        get => _value;
        set => SetValue_(value ?? "");
    }

    private void StartRecording()
    {
        _recording = true;
        _beforeEdit = _value;
        Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF6, 0xFF));
        BorderBrush = RecordingBrush;
        Foreground = HintBrush;
        Text = "请按下快捷键（Backspace 清空 / Esc 取消）";
    }

    private void StopRecording()
    {
        _recording = false;
        Background = Brushes.White;
        BorderBrush = IdleBrush;
        Foreground = TextBrush;
        SetValue_(_value);
    }

    private void SetValue_(string text)
    {
        _value = text;
        Foreground = string.IsNullOrWhiteSpace(text) ? HintBrush : TextBrush;
        Text = string.IsNullOrWhiteSpace(text) ? "(未设置)" : text;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording)
            return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete)
        {
            SetValue_("");
            return;
        }
        if (key == Key.Escape)
        {
            SetValue_(_beforeEdit);
            StopRecording();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return; // 纯修饰键不构成热键，等待后续按键

        // 任意键均可录入（数字/字母单键、功能键、任意组合），修饰键按实际按下状态记录
        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        SetValue_(string.Join("+", parts));
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
