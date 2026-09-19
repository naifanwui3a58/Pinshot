using System.Drawing;

namespace Pinshot.Core;

/// <summary>
/// 像素梯度边界检测（第三级兜底）：UIA 与窗口都拿不到时（自绘程序/桌面），
/// 以光标颜色为种子做受限 flood-fill，推断内容块的边界。
/// 带节流与结果缓存，避免 16ms 定时器高频扫描。
/// </summary>
internal static class PixelEdgeProbe
{
    private const int ScanWindowSize = 161;   // 扫描窗（物理像素）
    private const int ColorTolerance = 46;    // 颜色距离阈值
    private const int CacheMs = 300;

    private static readonly object Gate = new();
    private static Rectangle _last = Rectangle.Empty;
    private static Point _lastPoint;
    private static DateTime _lastTime = DateTime.MinValue;

    public static Rectangle DetectRegion(Bitmap frozen, Rectangle monitorBounds, System.Drawing.Point global)
    {
        lock (Gate)
        {
            if (Math.Abs(_lastPoint.X - global.X) < 6 && Math.Abs(_lastPoint.Y - global.Y) < 6 &&
                (DateTime.Now - _lastTime).TotalMilliseconds < CacheMs)
                return _last;
        }

        var result = Detect(frozen, monitorBounds, global);
        lock (Gate)
        {
            _last = result;
            _lastPoint = global;
            _lastTime = DateTime.Now;
        }
        return result;
    }

    private static Rectangle Detect(Bitmap frozen, Rectangle monitorBounds, System.Drawing.Point global)
    {
        var local = new Point(global.X - monitorBounds.Left, global.Y - monitorBounds.Top);
        if (local.X < 0 || local.Y < 0 || local.X >= frozen.Width || local.Y >= frozen.Height)
            return Rectangle.Empty;

        // 扫描窗以光标为中心、夹在位图内
        var half = ScanWindowSize / 2;
        var winX = Math.Clamp(local.X - half, 0, frozen.Width - 1);
        var winY = Math.Clamp(local.Y - half, 0, frozen.Height - 1);
        var winW = Math.Min(ScanWindowSize, frozen.Width - winX);
        var winH = Math.Min(ScanWindowSize, frozen.Height - winY);
        if (winW < 8 || winH < 8)
            return Rectangle.Empty;

        var seed = frozen.GetPixel(local.X, local.Y);
        var region = new bool[winW, winH];
        var queue = new Queue<Point>();
        queue.Enqueue(new Point(local.X - winX, local.Y - winY));
        region[local.X - winX, local.Y - winY] = true;

        var minX = winW; var minY = winH; var maxX = 0; var maxY = 0;
        var count = 0;
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            count++;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X + 1 > maxX) maxX = p.X + 1;
            if (p.Y + 1 > maxY) maxY = p.Y + 1;

            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = p.X + dx;
                var ny = p.Y + dy;
                if (nx < 0 || ny < 0 || nx >= winW || ny >= winH || region[nx, ny])
                    continue;
                var c = frozen.GetPixel(winX + nx, winY + ny);
                if (ColorDistance(c, seed) > ColorTolerance)
                    continue;
                region[nx, ny] = true;
                queue.Enqueue(new Point(nx, ny));
            }
        }

        // 区域太小（纯背景）或太大（几乎整窗）都视为无有效边界
        if (count < 64 || maxX - minX < 16 || maxY - minY < 16)
            return Rectangle.Empty;
        if ((long)(maxX - minX) * (maxY - minY) > (long)winW * winH * 9 / 10)
            return Rectangle.Empty;

        return Rectangle.FromLTRB(
            monitorBounds.Left + winX + minX,
            monitorBounds.Top + winY + minY,
            monitorBounds.Left + winX + maxX,
            monitorBounds.Top + winY + maxY);
    }

    private static int ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return (int)Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
