using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Pinshot.Core;

public sealed class Config
{
    /// <summary>截图贴图热键（WPF 键手势字符串，如 Alt+Shift+P）。</summary>
    public string HotkeyPin { get; set; } = "Alt+Shift+P";

    /// <summary>显示/隐藏所有贴图热键。</summary>
    public string HotkeyToggleVisibility { get; set; } = "Alt+Shift+H";

    /// <summary>小图自动放大后再识别（提升小字识别率）。</summary>
    public bool OcrUpscale { get; set; } = true;

    /// <summary>截图完成后转为贴图（可与“复制到剪贴板”共存；都关时按转为贴图兜底）。</summary>
    public bool CaptureAfterPin { get; set; } = true;

    /// <summary>截图完成后同时复制到剪贴板。</summary>
    public bool CaptureAfterCopy { get; set; } = false;

    /// <summary>OCR 语言标签（如 zh-Hans-CN / en-US / ja），空 = 跟随系统用户语言。</summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>图片翻译：译文按原文字行原位覆盖在图片上。</summary>
    public bool TranslateOverlay { get; set; } = true;

    /// <summary>原文译文对照翻译：翻译完成时弹出对照窗口（上原文下译文）。</summary>
    public bool TranslateShowCompareWindow { get; set; } = true;

    /// <summary>对照翻译窗口：显示原文（子选项；与显示译文都关时按都开处理，避免空窗口）。</summary>
    public bool CompareShowSource { get; set; } = true;

    /// <summary>对照翻译窗口：显示译文（子选项）。</summary>
    public bool CompareShowTranslation { get; set; } = true;

    /// <summary>常用 OCR 语言包是否已完成一次性批量安装（避免每次启动重复提权）。</summary>
    public bool OcrPacksInstalled { get; set; } = false;

    /// <summary>翻译提供方：google（免费免配置）/ openai（OpenAI 兼容接口）/ deepl。</summary>
    public string TranslateProvider { get; set; } = "auto";

    /// <summary>免费接口链的目标语言（zh-CN / en / ja ...）。</summary>
    public string FreeTargetLang { get; set; } = "zh-CN";

    /// <summary>启用的免费翻译引擎（按顺序尝试）：youdao / mymemory / google / baidu / custom。</summary>
    public List<string> FreeEngines { get; set; } = ["youdao", "mymemory", "google"];

    /// <summary>百度翻译开放平台 APPID（https://fanyi-api.baidu.com 免费申请）。</summary>
    public string BaiduAppId { get; set; } = "";

    /// <summary>百度翻译密钥。</summary>
    public string BaiduSecret { get; set; } = "";

    /// <summary>自定义翻译接口 URL 模板，支持占位符 {text} {to}，如 https://api.example.com/tr?text={text}&to={to}</summary>
    public string CustomUrl { get; set; } = "";

    public GoogleConfig Google { get; set; } = new();

    /// <summary>重启后恢复未关闭的贴图（Setuna 行为）。</summary>
    public bool PersistPins { get; set; } = true;

    /// <summary>以管理员身份运行（下次启动自提升，UAC 确认）。</summary>
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>启动时显示主面板（截取/翻译/选项）。</summary>
    public bool ShowPanelOnStartup { get; set; } = true;

    /// <summary>双击贴图动作：Compact（收缩）/ Close（关闭）。</summary>
    public string DoubleClickAction { get; set; } = "Compact";

    /// <summary>截图时显示全屏十字光标（Snipaste 式）。</summary>
    public bool CaptureCrosshair { get; set; } = true;

    /// <summary>截图时显示放大镜。</summary>
    public bool CaptureMagnifier { get; set; } = true;

    /// <summary>截图中保留鼠标指针（把光标画进截图）。</summary>
    public bool CaptureIncludeCursor { get; set; } = false;

    /// <summary>回收站容量（最近关闭的贴图保留数）。</summary>
    public int DustboxCapacity { get; set; } = 8;

    /// <summary>右键菜单条目可见性（key → 是否显示），缺省视为显示。</summary>
    public Dictionary<string, bool> MenuVisibility { get; set; } = new();

    /// <summary>托盘菜单条目可见性（key → 是否显示），缺省视为显示。</summary>
    public Dictionary<string, bool> TrayVisibility { get; set; } = new();

    /// <summary>托盘菜单条目顺序（key 列表，按用户拖拽排序保存）。</summary>
    public List<string> TrayOrder { get; set; } = [];

    /// <summary>拖动贴图时保持半透明（Setuna 行为）。</summary>
    public bool DragSemiTransparent { get; set; } = true;

    /// <summary>新贴图默认边框：None（无边框）/ Mono（单色边框）。</summary>
    public string DefaultBorderStyle { get; set; } = "None";

    public OpenAiConfig OpenAI { get; set; } = new();

    public DeepLConfig DeepL { get; set; } = new();

    public sealed class GoogleConfig
    {
        /// <summary>目标语言代码（zh-CN / en / ja ...）。</summary>
        public string TargetLang { get; set; } = "zh-CN";
    }

    public sealed class OpenAiConfig
    {
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-4o-mini";

        /// <summary>目标语言（自然语言描述，如“中文”“English”）。</summary>
        public string TargetLang { get; set; } = "中文";
    }

    public sealed class DeepLConfig
    {
        public string AuthKey { get; set; } = "";

        /// <summary>目标语言代码（ZH / EN / JA ...）。</summary>
        public string TargetLang { get; set; } = "ZH";

        /// <summary>true 使用免费版 api-free.deepl.com。</summary>
        public bool UseFreeApi { get; set; } = true;
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pinshot");

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch
        {
            // 配置损坏时回退默认值，避免启动失败
        }
        return new Config();
    }

    public static void Save(Config config)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
    }

    /// <summary>热键字符串校验：合法返回 null，否则返回错误提示。支持任意单键（D1 / F5 / A 等）。</summary>
    public static string? ValidateHotkey(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return null;
        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], out _))
            return $"热键格式不正确：{hotkey}（示例：Alt+Shift+P 或 D1）";
        foreach (var modifier in parts[..^1])
        {
            if (modifier.ToLowerInvariant() is not ("ctrl" or "control" or "alt" or "shift" or "win" or "windows"))
                return $"热键格式不正确：{hotkey}（修饰键只能是 Ctrl/Alt/Shift/Win）";
        }
        return null;
    }
}
