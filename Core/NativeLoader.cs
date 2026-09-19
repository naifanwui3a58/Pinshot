using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Pinshot.Core;

/// <summary>
/// Paddle native 依赖的释放与预加载：
/// exe 内嵌 mkldnn/mklml/paddle_inference/libiomp5md 四个 DLL，
/// 启动时释放到 %LOCALAPPDATA%\Pinshot\native 并按依赖顺序用绝对路径预加载——
/// 之后 Paddle 用裸名 LoadLibrary 时直接命中已加载模块，
/// 彻底绕开单文件发布下 PATH/解压目录的搜索问题。
/// </summary>
internal static class NativeLoader
{
    private const string ResourcePrefix = "Pinshot.Resources.Native.";

    private static readonly string[] Dlls =
    [
        "libiomp5md.dll",
        "mklml.dll",
        "mkldnn.dll",
        "paddle_inference.dll",
    ];

    public static string NativeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pinshot", "native");

    /// <summary>释放 DLL 并按依赖顺序预加载。失败静默（Paddle 初始化失败时回退 Windows OCR）。</summary>
    public static void ExtractAndPreload()
    {
        try
        {
            Directory.CreateDirectory(NativeDir);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var dll in Dlls)
            {
                var target = Path.Combine(NativeDir, dll);
                var resourceName = ResourcePrefix + dll;
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return; // 包里没有（老版本）→ 跳过整个预加载
                    if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
                    {
                        using var output = File.Create(target);
                        stream.CopyTo(output);
                    }
                }
            }

            // 依赖顺序预加载：LoadLibrary 绝对路径后，进程内按裸名再加载直接命中
            foreach (var dll in Dlls)
                Win32.LoadLibraryW(Path.Combine(NativeDir, dll));
        }
        catch
        {
            // 预加载失败不影响主流程：Paddle 初始化失败时自动回退 Windows OCR
        }
    }
}
