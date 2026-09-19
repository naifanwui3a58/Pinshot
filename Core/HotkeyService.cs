using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Pinshot.Core;

/// <summary>
/// 全局热键服务：直接封装 Win32 RegisterHotKey/UnregisterHotKey（微信、Snipaste 同款底层 API）。
/// 支持任意单键（数字 1、字母 A、F5、Space 等），不依赖 WPF KeyGesture 的修饰键限制。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly Dictionary<string, (int Id, Key Key, ModifierKeys Modifiers, Action Action)> _hotkeys = [];
    private HwndSource? _source;
    private nint _hwnd;
    private int _nextId = 1;

    public void Register(string name, Key key, ModifierKeys modifiers, Action action)
    {
        EnsureWindow();
        Unregister(name);

        var id = _nextId++;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        // Win32 修饰位：Alt=1 Ctrl=2 Shift=4 Win=8；None=0（单键合法）
        var mods = (modifiers.HasFlag(ModifierKeys.Alt) ? 1 : 0) |
                   (modifiers.HasFlag(ModifierKeys.Control) ? 2 : 0) |
                   (modifiers.HasFlag(ModifierKeys.Shift) ? 4 : 0) |
                   (modifiers.HasFlag(ModifierKeys.Windows) ? 8 : 0);

        if (!Win32.RegisterHotKey(_hwnd, id, (uint)mods, (uint)vk))
            return; // 与其他程序冲突：静默放弃（与原行为一致）

        _hotkeys[name] = (id, key, modifiers, action);
    }

    public void Unregister(string name)
    {
        if (!_hotkeys.TryGetValue(name, out var existing))
            return;
        Win32.UnregisterHotKey(_hwnd, existing.Id);
        _hotkeys.Remove(name);
    }

    public void Dispose()
    {
        foreach (var (_, (id, _, _, _)) in _hotkeys)
            Win32.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        _hwnd = nint.Zero;
    }

    private void EnsureWindow()
    {
        if (_source != null)
            return;
        _source = new HwndSource(new HwndSourceParameters("PinshotHotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = Win32.GetDesktopWindow(),
        });
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
            return nint.Zero;
        handled = true;
        var id = (int)wParam;
        foreach (var (_, record) in _hotkeys)
        {
            if (record.Id != id)
                continue;
            // 回到 UI 线程执行动作
            Application.Current?.Dispatcher.BeginInvoke(record.Action);
            break;
        }
        return nint.Zero;
    }
}
