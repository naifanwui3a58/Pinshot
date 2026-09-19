using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Pinshot.Core;

/// <summary>
/// PaddleOCR 本地引擎封装：离线、免费，中文/数字/英文识别率远高于 Windows OCR。
/// 引擎首次初始化较慢（约 1-3 秒），进程内单例复用；识别走锁串行（底层非线程安全）。
/// 初始化或识别失败自动回退 Windows OCR。
/// </summary>
public static class PaddleOcrEngine
{
    private static readonly object Gate = new();
    private static PaddleOCRSharp.PaddleOCREngine? _engine;
    private static bool _failed;

    /// <summary>后台预热引擎（启动时调用，避免首次识别卡顿）。</summary>
    public static void Warmup()
    {
        if (_failed)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                _ = GetEngine();
            }
            catch
            {
                // 预热失败：使用时再试一次，仍失败则回退 Windows OCR
            }
        });
    }

    private static PaddleOCRSharp.PaddleOCREngine GetEngine()
    {
        if (_engine != null)
            return _engine;
        // 检测参数调优：开启膨胀 + 降低检测框阈值，明显减少小字/短词漏识别（如 dll→d）
        var parameter = new PaddleOCRSharp.OCRParameter
        {
            use_dilation = true,
            det_db_box_thresh = 0.3f,
            det_db_unclip_ratio = 1.8f,
            cpu_math_library_num_threads = 4,
        };
        // 模型指向释放到本地的内嵌副本（单文件发布无需随包拖 inference 文件夹）
        var modelsDir = PaddleModels.ModelsDir;
        var modelConfig = new PaddleOCRSharp.OCRModelConfig
        {
            det_infer = System.IO.Path.Combine(modelsDir, "ch_PP-OCRv4_det_infer"),
            cls_infer = System.IO.Path.Combine(modelsDir, "ch_ppocr_mobile_v2.0_cls_infer"),
            rec_infer = System.IO.Path.Combine(modelsDir, "ch_PP-OCRv4_rec_infer"),
            keys = System.IO.Path.Combine(modelsDir, "ppocr_keys.txt"),
        };
        return _engine ??= new PaddleOCRSharp.PaddleOCREngine(modelConfig, parameter);
    }

    /// <summary>是否可用（引擎能否初始化）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_failed)
                return false;
            try
            {
                _ = GetEngine();
                return true;
            }
            catch
            {
                _failed = true;
                return false;
            }
        }
    }

    /// <summary>识别位图：返回逐行文字 + 像素坐标。失败抛异常（调用方回退 Windows OCR）。</summary>
    public static List<OcrLine> Recognize(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();

        lock (Gate)
        {
            var engine = GetEngine();
            var result = engine.DetectText(bytes);
            if (result?.TextBlocks == null || result.TextBlocks.Count == 0)
                return [];

            var lines = new List<OcrLine>();
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.BoxPoints is not { Count: > 0 })
                    continue;
                var left = block.BoxPoints.Min(p => p.X);
                var top = block.BoxPoints.Min(p => p.Y);
                var right = block.BoxPoints.Max(p => p.X);
                var bottom = block.BoxPoints.Max(p => p.Y);
                var rect = new RectangleF(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
                lines.Add(new OcrLine(block.Text.Trim(), rect, rect.Height));
            }
            // Paddle 返回顺序为从上到下扫描；按 top 排序保证阅读顺序稳定
            lines.Sort((a, b) => a.Rect.Top.CompareTo(b.Rect.Top));

            // 同行碎块合并：检测框把一个词拆成多段（如 dll → "dl." + "l"）时，
            // 垂直重叠大且水平间距小于一个字宽的相邻块合并回一个词
            var merged = new List<OcrLine>();
            foreach (var line in lines)
            {
                if (merged.Count > 0 && TryMergeWith(merged[^1], line))
                    merged[^1] = Merge(merged[^1], line);
                else
                    merged.Add(line);
            }
            return merged;
        }
    }

    /// <summary>同行相邻块合并：垂直重叠过半且水平间隙小于 0.9 倍行高。</summary>
    private static bool TryMergeWith(OcrLine left, OcrLine right)
    {
        var overlapTop = Math.Max(left.Rect.Top, right.Rect.Top);
        var overlapBottom = Math.Min(left.Rect.Bottom, right.Rect.Bottom);
        var verticalOverlap = overlapBottom - overlapTop;
        var minHeight = Math.Min(left.Rect.Height, right.Rect.Height);
        if (verticalOverlap < minHeight * 0.5)
            return false;
        var gap = right.Rect.Left - left.Rect.Right;
        if (gap > minHeight * 0.9 || gap < -minHeight * 2)
            return false;
        return true;
    }

    /// <summary>合并两个同行块为新块（record 不可变，替换而非原地改）。</summary>
    private static OcrLine Merge(OcrLine left, OcrLine right)
    {
        var union = System.Drawing.RectangleF.Union(left.Rect, right.Rect);
        return new OcrLine(
            left.Text + right.Text,
            union,
            Math.Max(left.LineHeight, right.LineHeight));
    }
}
