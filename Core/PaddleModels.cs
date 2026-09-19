using System.IO;
using System.Reflection;

namespace Pinshot.Core;

/// <summary>
/// PaddleOCR 模型释放：模型文件内嵌在 exe 资源里（Resources/Models），
/// 首次启动时释放到 %LOCALAPPDATA%\Pinshot\inference，之后引擎直接指向该目录。
/// 这样发布物就是一个单文件 exe，无需随包拖 inference 文件夹。
/// </summary>
internal static class PaddleModels
{
    private const string ResourcePrefix = "Pinshot.Resources.Models.";

    /// <summary>(资源名后缀, 相对路径) —— 文件名带点，必须显式映射。</summary>
    private static readonly (string ResourceSuffix, string RelativePath)[] Files =
    [
        // 注意：MSBuild 会把资源名里的 '-' 及文件夹名中的 '.' 归一化为 '_'，
        // 因此资源后缀与磁盘相对路径不同（如 ch_PP-OCRv4 → ch_PP_OCRv4）
        ("ch_PP_OCRv4_det_infer.inference.pdmodel", @"ch_PP-OCRv4_det_infer\inference.pdmodel"),
        ("ch_PP_OCRv4_det_infer.inference.pdiparams", @"ch_PP-OCRv4_det_infer\inference.pdiparams"),
        ("ch_PP_OCRv4_det_infer.inference.pdiparams.info", @"ch_PP-OCRv4_det_infer\inference.pdiparams.info"),
        ("ch_PP_OCRv4_rec_infer.inference.pdmodel", @"ch_PP-OCRv4_rec_infer\inference.pdmodel"),
        ("ch_PP_OCRv4_rec_infer.inference.pdiparams", @"ch_PP-OCRv4_rec_infer\inference.pdiparams"),
        ("ch_PP_OCRv4_rec_infer.inference.pdiparams.info", @"ch_PP-OCRv4_rec_infer\inference.pdiparams.info"),
        ("ch_ppocr_mobile_v2._0_cls_infer.inference.pdmodel", @"ch_ppocr_mobile_v2.0_cls_infer\inference.pdmodel"),
        ("ch_ppocr_mobile_v2._0_cls_infer.inference.pdiparams", @"ch_ppocr_mobile_v2.0_cls_infer\inference.pdiparams"),
        ("ch_ppocr_mobile_v2._0_cls_infer.inference.pdiparams.info", @"ch_ppocr_mobile_v2.0_cls_infer\inference.pdiparams.info"),
        ("ppocr_keys.txt", "ppocr_keys.txt"),
        ("PaddleOCR.config.json", "PaddleOCR.config.json"),
    ];

    /// <summary>模型释放目录。</summary>
    public static string ModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pinshot", "inference");

    /// <summary>确保模型已释放到本地磁盘（缺哪个补哪个，已存在且大小一致则跳过）。</summary>
    public static void EnsureExtracted()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(ModelsDir);
        foreach (var (suffix, relativePath) in Files)
        {
            var target = Path.Combine(ModelsDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var resourceName = ResourcePrefix + suffix;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException(
                    "内嵌模型资源缺失：" + resourceName + "；可用资源："
                    + string.Join(" | ", assembly.GetManifestResourceNames()));

            // 已存在且大小一致 → 跳过（避免每次启动重写 17MB）
            if (File.Exists(target))
            {
                try
                {
                    var existing = new FileInfo(target);
                    if (existing.Length == stream.Length)
                        continue;
                }
                catch
                {
                    // 读取失败则重写
                }
            }

            using var output = File.Create(target);
            stream.CopyTo(output);
        }
    }
}
