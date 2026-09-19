using System.Drawing;
using System.Windows.Automation;

namespace Pinshot.Core;

/// <summary>
/// UI 自动化元素检测（Snipaste/PixPin 同路线）：鼠标位置 UIA 命中测试，
/// 识别窗口内部的视频、图片、标题等子元素边界。
///
/// 浏览器（Chrome/Edge/Firefox）的辅助功能树是"惰性唤醒"的：必须先向其窗口发送
/// WM_GETOBJECT(OBJID_CLIENT) 激活信号，深层的视频/图片/标题节点才会暴露。
/// 因此这里在每次探测前先发激活信号（向顶层窗口与其渲染子窗口），再做多轮
/// FromPoint 并保留最小非空边界。对所有浏览器通用，不针对任何一家单独处理。
/// </summary>
internal static class UiaProbe
{
    private const int Attempts = 4;         // 每轮探测尝试次数
    private const int AttemptGapMs = 160;   // 尝试间隔（等浏览器建辅助功能树）
    private const int CacheMs = 350;        // 缓存有效期

    private const uint WM_GETOBJECT = 0x003D;

    private static readonly object Gate = new();
    private static Rectangle _last = Rectangle.Empty;
    private static Point _lastPoint;
    private static nint _lastHwnd;
    private static DateTime _lastTime = DateTime.MinValue;
    private static bool _busy;

    public static Rectangle GetElementRect(System.Drawing.Point screenPoint, nint windowHandle)
    {
        lock (Gate)
        {
            if (Math.Abs(_lastPoint.X - screenPoint.X) < 6 &&
                Math.Abs(_lastPoint.Y - screenPoint.Y) < 6 &&
                _lastHwnd == windowHandle &&
                (DateTime.Now - _lastTime).TotalMilliseconds < CacheMs)
                return _last;
            if (_busy)
                return _last;
            _busy = true;
        }

        var captured = screenPoint;
        var capturedHwnd = windowHandle;
        Task.Run(() =>
        {
            try
            {
                // 激活信号：浏览器收到 OBJID_CLIENT 的 WM_GETOBJECT 才会暴露深层元素
                if (capturedHwnd != 0)
                {
                    Win32.SendMessageW(capturedHwnd, WM_GETOBJECT, 0, (nint)(-4));
                    var renderer = Win32.FindWindowExW(capturedHwnd, 0, "Chrome_RenderWidgetHostHWND", null);
                    if (renderer != 0)
                        Win32.SendMessageW(renderer, WM_GETOBJECT, 0, (nint)(-4));
                }

                var best = Rectangle.Empty;
                for (var attempt = 0; attempt < Attempts; attempt++)
                {
                    var rect = ProbeOnce(captured);
                    if (!rect.IsEmpty)
                    {
                        if (best.IsEmpty || Area(rect) < Area(best))
                            best = rect;
                        if (best.Width <= 240 && best.Height <= 240)
                            break;
                    }
                    Thread.Sleep(AttemptGapMs);
                }

                lock (Gate)
                {
                    _last = best;
                    _lastPoint = captured;
                    _lastTime = DateTime.Now;
                }
            }
            finally
            {
                lock (Gate)
                    _busy = false;
            }
        });

        lock (Gate)
            return _last;
    }

    private static Rectangle ProbeOnce(System.Drawing.Point pt)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (element == null)
                return Rectangle.Empty;
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8)
                return Rectangle.Empty;
            return Rectangle.FromLTRB(
                (int)rect.Left, (int)rect.Top, (int)rect.Right, (int)rect.Bottom);
        }
        catch
        {
            return Rectangle.Empty;
        }
    }

    private static long Area(Rectangle r) => (long)r.Width * r.Height;
}
