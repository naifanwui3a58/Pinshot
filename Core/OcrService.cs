using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace Pinshot.Core;

public sealed record OcrResult(string Text, string LanguageTag)
{
    /// <summary>逐行结果，Rect 为提交位图像素坐标。</summary>
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];
}

public sealed record OcrLine(string Text, System.Drawing.RectangleF Rect, float LineHeight)
{
    public OcrLine(string Text, System.Drawing.RectangleF Rect) : this(Text, Rect, Rect.Height) { }
}

/// <summary>
/// 基于 Windows.Media.Ocr 的离线文字识别。
/// 识别语言取决于系统已安装的 OCR 语言包（Windows 设置 → 时间和语言 → 语言 → 添加语言）。
/// </summary>
public static class OcrService
{
    public static IReadOnlyList<string> GetAvailableLanguages() =>
        [.. OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];

    /// <summary>支持的全部 OCR 语言（共 5 种）。语言包由应用启动时自动批量装齐，设置里直接可选。</summary>
    public static readonly (string Tag, string Display)[] OcrLanguages =
    [
        ("zh-Hans-CN", "中文（简体）"), ("en-US", "English"), ("ja", "日本語"),
        ("ko", "한국어"), ("ru", "Русский"),
    ];

    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
            if (engine != null)
                return engine;
        }
        return OcrEngine.TryCreateFromUserProfileLanguages();
    }


    /// <summary>
    /// OCR 预处理放大倍数：短边 &lt;400 放大 3 倍、&lt;1000 放大 2 倍——编辑器标签/菜单这类
    /// 密集小字在原生分辨率下最容易丢字符（丢首字母/标点），放大后识别率明显提升；
    /// 短边 ≥1000 或超大幅面返回 1（直接用原图，控制推理耗时与内存）。
    /// </summary>
    private static int GetUpscaleScale(System.Drawing.Bitmap bitmap)
    {
        var shortSide = Math.Min(bitmap.Width, bitmap.Height);
        if (shortSide >= 1000 || (long)bitmap.Width * bitmap.Height > 16_000_000)
            return 1;
        return shortSide < 400 ? 3 : 2;
    }

    /// <summary>按倍数生成放大副本（高质量插值）。返回新位图，所有权归调用方。</summary>
    private static System.Drawing.Bitmap PrepareScaled(System.Drawing.Bitmap bitmap, int scale)
    {
        var upscaled = new System.Drawing.Bitmap(
            bitmap.Width * scale, bitmap.Height * scale,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(upscaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(bitmap, 0, 0, upscaled.Width, upscaled.Height);
        return upscaled;
    }

    public static async Task<OcrResult> RecognizeAsync(Bitmap bitmap, string? languageTag)
    {
        // PaddleOCR 优先：本地离线、中英数字识别率高；失败回退 Windows OCR
        try
        {
            // 放大识别用临时副本（用完即弃）；调用方的 bitmap 归调用方所有（贴图后续
            // 还要取色/提取/再翻译），绝不能在这里被 Dispose——否则该贴图下次 OCR
            // 会抛“Parameter is not valid.”
            var scale = App.Config.OcrUpscale ? GetUpscaleScale(bitmap) : 1;
            using var paddlePrepared = scale > 1 ? PrepareScaled(bitmap, scale) : null;
            var paddleLines = await Task.Run(() => PaddleOcrEngine.Recognize(paddlePrepared ?? bitmap));
            if (paddleLines.Count > 0)
            {
                var scaleBack = paddlePrepared == null
                    ? 1.0
                    : bitmap.Width / (double)paddlePrepared.Width;
                var scaledLines = paddleLines.Select(l => new OcrLine(
                    l.Text,
                    new RectangleF(
                        (float)(l.Rect.Left * scaleBack),
                        (float)(l.Rect.Top * scaleBack),
                        (float)(l.Rect.Width * scaleBack),
                        (float)(l.Rect.Height * scaleBack)),
                    (float)(l.LineHeight * scaleBack))).ToList();
                return new OcrResult(string.Join(Environment.NewLine, scaledLines.Select(l => l.Text)), "paddle")
                {
                    Lines = scaledLines,
                };
            }
        }
        catch
        {
            // Paddle 不可用：回退 Windows OCR
        }

        var engine = CreateEngine(languageTag)
            ?? throw new InvalidOperationException(
                "系统没有可用的 OCR 语言包。请在 Windows 设置 → 时间和语言 → 语言 中安装对应语言包（含“基本拼写检查”/OCR 组件）。");

        // 识别前预处理（对齐微信式体验的关键）：小图放大可显著提升 Windows OCR 命中率，
        // 文字过小的截图（聊天记录、状态栏）不做放大经常整段漏识别；同样只用临时副本
        var wscale = App.Config.OcrUpscale ? GetUpscaleScale(bitmap) : 1;
        using var prepared = wscale > 1 ? PrepareScaled(bitmap, wscale) : null;

        // GDI 位图 → PNG 内存流 → WinRT 解码 → SoftwareBitmap
        // 注意：不能用 AsStreamForWrite 的包装流（dispose 时会连带释放底层 WinRT 流，
        // 导致后续 Seek 抛 ObjectDisposedException），直接用 AsRandomAccessStream 包装视图
        using var pngStream = new MemoryStream();
        (prepared ?? bitmap).Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        pngStream.Position = 0;

        using var randomAccessStream = pngStream.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(softwareBitmap);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
                continue;
            var union = line.Words[0].BoundingRect;
            foreach (var word in line.Words.Skip(1))
            {
                var rect = word.BoundingRect;
                var left = Math.Min(union.X, rect.X);
                var top = Math.Min(union.Y, rect.Y);
                var right = Math.Max(union.X + union.Width, rect.X + rect.Width);
                var bottom = Math.Max(union.Y + union.Height, rect.Y + rect.Height);
                union = new Windows.Foundation.Rect(left, top, right - left, bottom - top);
            }
            lines.Add(new OcrLine(
                line.Text,
                new System.Drawing.RectangleF((float)union.X, (float)union.Y, (float)union.Width, (float)union.Height),
                (float)union.Height));
        }

        return new OcrResult(string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)),
            engine.RecognizerLanguage.LanguageTag)
        {
            Lines = lines,
        };
    }
}
