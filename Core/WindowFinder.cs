using System.Runtime.InteropServices;
using System.Windows;

namespace Pinshot.Core;

/// <summary>
/// 枚举当前可见的顶层窗口及其物理屏幕区域（Snipaste 式窗口自动检测的数据源）。
/// 过滤：不可见、最小化、零尺寸、被 DWM 遮蔽（UWP 挂起）、工具窗口、自身进程。
/// </summary>
internal static partial class Win32
{
    public struct WINDOWINFO
    {
        public uint cbSize;
        public RECT rcWindow;
        public RECT rcClient;
        public uint dwStyle;
        public uint dwExStyle;
        public uint dwWindowStatus;
        public uint cxWindowBorders;
        public uint cyWindowBorders;
        public ushort atomWindowType;
        public ushort wCreatorVersion;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowInfo(nint hwnd, ref WINDOWINFO info);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint hwnd, char* text, int count);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private static unsafe string GetWindowTitle(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* pointer = buffer)
        {
            var length = GetWindowText(hwnd, pointer, buffer.Length);
            return length > 0 ? new string(buffer[..length]) : string.Empty;
        }
    }

    /// <summary>枚举可用于截图吸附的窗口区域（物理像素，DWM 可视边界）。</summary>
    public static List<WindowHit> EnumWindowBounds()
    {
        var results = new List<WindowHit>();
        var selfPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd))
                    return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == selfPid)
                    return true;

                var info = new WINDOWINFO { cbSize = (uint)Marshal.SizeOf<WINDOWINFO>() };
                if (!GetWindowInfo(hwnd, ref info))
                    return true;

                const uint WS_EX_TOOLWINDOW = 0x00000080;
                const uint WS_MINIMIZE = 0x20000000;
                if ((info.dwExStyle & WS_EX_TOOLWINDOW) != 0 || (info.dwStyle & WS_MINIMIZE) != 0)
                    return true;

                // 用 DWM 可视边界（不含不可见的阴影边距）
                var bounds = GetDwmWindowBounds(hwnd);
                if (bounds.IsEmpty)
                    bounds = new System.Drawing.Rectangle(
                        info.rcWindow.Left, info.rcWindow.Top,
                        info.rcWindow.Right - info.rcWindow.Left,
                        info.rcWindow.Bottom - info.rcWindow.Top);
                if (bounds.Width < 40 || bounds.Height < 40)
                    return true;
                if (!IntersectsVirtualScreen(bounds))
                    return true;

                var title = GetWindowTitle(hwnd);
                if (title.Length == 0 && (info.dwStyle & 0x00C00000) == 0)
                    return true; // 无标题且无标题栏的悬浮窗多为辅助窗口

                results.Add(new WindowHit(hwnd, bounds));
            }
            catch
            {
                // 单个窗口失败跳过
            }
            return true;
        }, nint.Zero);

        // Z 序在前的优先（EnumWindows 自顶向下，保持顺序即 Z 序）
        return results;
    }

    private static bool IntersectsVirtualScreen(System.Drawing.Rectangle bounds) =>
        bounds.Right > SystemParameters.VirtualScreenLeft &&
        bounds.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        bounds.Bottom > SystemParameters.VirtualScreenTop &&
        bounds.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    /// <summary>取窗口 DWM 可视边界；失败返回 Empty。</summary>
    public static System.Drawing.Rectangle GetDwmWindowBounds(nint hwnd)
    {
        try
        {
            var rect = new RECT();
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ref rect, Marshal.SizeOf<RECT>()) == 0)
                return new System.Drawing.Rectangle(rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        catch
        {
            // DWM 不可用时回退 GetWindowInfo
        }
        return System.Drawing.Rectangle.Empty;
    }
}

internal static partial class Win32
{
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 13;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int attr, ref RECT rect, int size);
}

public sealed record WindowHit(nint Handle, System.Drawing.Rectangle Bounds);
