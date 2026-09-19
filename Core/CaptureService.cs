using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Pinshot.Views;

namespace Pinshot.Core;

public sealed record CaptureResult(Bitmap Bitmap, Rectangle PhysicalBounds);

/// <summary>
/// 自研截图入口（Snipaste 式）：冻结各显示器画面，全屏覆盖层上悬停自动吸附窗口、
/// 拖拽自定义选区、十字光标、放大镜、可选把鼠标指针画进截图。
/// </summary>
public static class CaptureService
{
    public static async Task<CaptureResult?> CaptureAsync()
    {
        // 已有贴图先 DWM 遮蔽，避免被截进新图；同一时刻只允许一次截图
        await using var lease = await App.Pins.BeginCaptureAsync();
        if (lease == null)
            return null;

        var config = App.Config;
        var monitors = GetMonitorRects();
        if (monitors.Count == 0)
            return null;

        // 让遮蔽与面板隐藏先完成渲染
        await Task.Delay(120);

        try
        {
            // 冻结屏幕（可选画入鼠标指针）
            var freezes = new List<(Rectangle Bounds, Bitmap Bitmap)>();
            foreach (var bounds in monitors)
            {
                var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
                }
                freezes.Add((bounds, bitmap));
            }
            if (config.CaptureIncludeCursor)
                DrawCursorOnto(freezes);

            var windows = Win32.EnumWindowBounds();
            CaptureOverlayWindow.Reset();
            foreach (var (bounds, bitmap) in freezes)
            {
                var dpi = Win32Helper.GetDpiScaleForPhysicalPoint(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var overlay = new CaptureOverlayWindow(bounds, bitmap, windows, dpi,
                    config.CaptureCrosshair, config.CaptureMagnifier);
                overlay.Show();
            }
            var rect = await CaptureOverlayWindow.WaitSelectionAsync();
            CaptureOverlayWindow.CloseAll();

            if (rect == null)
            {
                foreach (var (_, bitmap) in freezes)
                    bitmap.Dispose();
                return null;
            }

            // 按选区从对应显示器的冻结图裁剪
            foreach (var (bounds, bitmap) in freezes)
            {
                var local = Rectangle.Intersect(Rectangle.FromLTRB(
                    rect.Value.Left - bounds.Left, rect.Value.Top - bounds.Top,
                    rect.Value.Right - bounds.Left, rect.Value.Bottom - bounds.Top),
                    new Rectangle(0, 0, bounds.Width, bounds.Height));
                if (local.Width > 0 && local.Height > 0)
                {
                    var cropped = bitmap.Clone(local, bitmap.PixelFormat);
                    foreach (var (_, other) in freezes)
                    {
                        if (!ReferenceEquals(other, bitmap))
                            other.Dispose();
                    }
                    return new CaptureResult(cropped, new Rectangle(
                        bounds.Left + local.Left, bounds.Top + local.Top, local.Width, local.Height));
                }
            }
            foreach (var (_, bitmap) in freezes)
                bitmap.Dispose();
            return null;
        }
        catch
        {
            CaptureOverlayWindow.CloseAll();
            return null;
        }
    }

    private static void DrawCursorOnto(List<(Rectangle Bounds, Bitmap Bitmap)> freezes)
    {
        if (!Win32Helper.TryGetCursorPosition(out var cursor))
            return;
        var handle = Win32.GetCursorHandle();
        if (handle == nint.Zero)
            return;
        foreach (var (bounds, bitmap) in freezes)
        {
            var localX = cursor.X - bounds.Left;
            var localY = cursor.Y - bounds.Top;
            if (localX < -64 || localY < -64 || localX > bounds.Width + 64 || localY > bounds.Height + 64)
                continue;
            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            Win32.DrawIcon(graphics.GetHdc(), localX, localY, handle);
            graphics.ReleaseHdc();
        }
    }

    private static List<Rectangle> GetMonitorRects()
    {
        var rects = new List<Rectangle>();
        foreach (var area in Win32.GetMonitorWorkAreasFull())
            rects.Add(area);
        return rects;
    }
}
