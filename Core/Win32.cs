using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices.Marshalling;

namespace Pinshot.Core;

internal static partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    internal static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadLibraryW(string path);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    internal static partial nint MonitorFromPointNative(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint hObject);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    [LibraryImport("Shcore.dll")]
    internal static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>以指定动词（如 runas 提权）启动程序，成功返回大于 32 的值。</summary>
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ShellExecute(nint hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>枚举所有显示器的工作区（物理像素）。</summary>
    internal static List<Rect> GetMonitorWorkAreas()
    {
        var areas = new List<Rect>();
        EnumDisplayMonitors(nint.Zero, nint.Zero,
            (monitor, _, ref rect, _) =>
            {
                try
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        areas.Add(new Rect(
                            info.rcWork.Left, info.rcWork.Top,
                            info.rcWork.Right - info.rcWork.Left,
                            info.rcWork.Bottom - info.rcWork.Top));
                    }
                }
                catch
                {
                    // 单个显示器失败忽略
                }
                return true;
            }, nint.Zero);
        return areas;
    }

    /// <summary>枚举所有显示器的完整区域（物理像素，截图冻结用）。</summary>
    public static List<System.Drawing.Rectangle> GetMonitorWorkAreasFull()
    {
        var areas = new List<System.Drawing.Rectangle>();
        EnumDisplayMonitors(nint.Zero, nint.Zero,
            (monitor, _, ref rect, _) =>
            {
                // 原生回调内抛出的异常无法被托管代码捕获，会直接终止进程 —— 必须就地兜住
                try
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        areas.Add(System.Drawing.Rectangle.FromLTRB(
                            info.rcMonitor.Left, info.rcMonitor.Top,
                            info.rcMonitor.Right, info.rcMonitor.Bottom));
                    }
                }
                catch
                {
                    // 单个显示器失败忽略
                }
                return true;
            }, nint.Zero);
        return areas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT pt;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CURSORINFO info);

    [LibraryImport("user32.dll", EntryPoint = "DrawIconEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(nint hdc, int x, int y, nint hIcon, int cx, int cy, uint step, nint hbrFlickerFree, uint flags);

    /// <summary>当前鼠标指针句柄（未显示返回 Zero）。</summary>
    internal static nint GetCursorHandle()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        const int CURSOR_SHOWING = 0x00000001;
        return GetCursorInfo(ref info) && (info.flags & CURSOR_SHOWING) != 0 ? info.hCursor : nint.Zero;
    }

    internal static bool DrawIcon(nint hdc, int x, int y, nint icon) =>
        DrawIconEx(hdc, x, y, icon, 0, 0, 0, nint.Zero, 0x00000003 /* DI_NORMAL */);
}

internal static class WndProcHelper
{
    /// <summary>给 WPF 窗口挂 Win32 消息钩子。</summary>
    internal static HwndSource AddWndProcHook(Window window, HwndSourceHook hook)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window)
                     ?? throw new InvalidOperationException("窗口尚未建立可视化源。");
        source.AddHook(hook);
        return source;
    }
}

internal static class Win32Helper
{
    private const int DWMWA_CLOAK = 13;
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    internal static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = Win32.GetWindowLongPtr(handle, GWL_EXSTYLE);
        Win32.SetWindowLongPtr(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
    }

    /// <summary>DWM 遮蔽：窗口对截图不可见但仍保持 Z 序与焦点状态。</summary>
    internal static bool SetWindowCloaked(Window window, bool cloaked)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var value = cloaked ? 1 : 0;
        return Win32.DwmSetWindowAttribute(handle, DWMWA_CLOAK, ref value, sizeof(int)) == 0;
    }

    internal static void SetWindowPhysicalBounds(Window window, int left, int top, int width, int height)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Win32.SetWindowPos(handle, nint.Zero, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    internal static void FlushDesktopComposition() => Win32.DwmFlush();

    internal static bool TryGetCursorPosition(out System.Drawing.Point point)
    {
        if (Win32.GetCursorPos(out var cursor))
        {
            point = new(cursor.X, cursor.Y);
            return true;
        }
        point = default;
        return false;
    }

    internal static DpiScale GetDpiScaleForPhysicalPoint(int x, int y)
    {
        var monitor = Win32.MonitorFromPointNative(new Win32.POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (monitor != nint.Zero && Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            return new DpiScale(dpiX / 96.0, dpiY / 96.0);
        return new DpiScale(1, 1);
    }

    /// <summary>窗口所在（或最近）显示器的工作区（物理像素）。</summary>
    internal static Rect GetNearestMonitorWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = Win32.MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero)
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return new Rect(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
    }
}
