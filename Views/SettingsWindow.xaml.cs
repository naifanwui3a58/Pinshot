using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pinshot.Core;

namespace Pinshot.Views;

/// <summary>
/// 选项窗口（Setuna 式左侧分组导航）：常规 / 截图 / 贴图 / 文字提取 / 翻译服务。
/// 保存后立即生效（热键重新注册）。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly Config _editing;

    private SettingsWindow(Config config)
    {
        _editing = System.Text.Json.JsonSerializer.Deserialize<Config>(
            System.Text.Json.JsonSerializer.Serialize(config)) ?? new Config();
        InitializeComponent();
        LoadUi();
        Closed += (_, _) => _instance = null;
    }

    internal static void ShowSingle()
    {
        if (_instance is { } existing)
        {
            existing.Activate();
            return;
        }
        _instance = new SettingsWindow(App.Config) { Owner = null };
        _instance.Show();
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 初始化期间 ListBox 设置 SelectedIndex 会提前触发本事件，
        // 此时右侧分区控件尚未创建，必须跳过，否则空引用导致设置窗口崩溃
        if (PART_SectionGeneral == null || PART_SectionCapture == null ||
            PART_SectionPin == null || PART_SectionMenu == null ||
            PART_SectionTranslate == null || PART_SectionTray == null)
            return;

        var index = PART_Nav.SelectedIndex;
        PART_SectionGeneral.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PART_SectionCapture.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PART_SectionPin.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PART_SectionMenu.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PART_SectionTranslate.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PART_SectionTray.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly (string Key, string Label)[] TrayItemKeys =
    [
        // —— 托盘菜单（与托盘右键菜单一一对应） ——
        ("capture", "截图"), ("translate", "翻译"), ("extract", "提取文字"), ("panel", "主面板"),
        ("fromfile", "从文件创建贴图"), ("toggle", "显示/隐藏所有贴图"), ("activate", "激活所有贴图"),
        ("closeall", "关闭所有贴图"), ("gallery_active", "参考图名单"), ("gallery_dust", "回收站"),
        ("gallery_clear", "清空回收站"),
        ("options", "选项"), ("persist", "重启后恢复贴图"),
        ("configdir", "打开配置文件夹"), ("exit", "退出"),
    ];

    private static readonly (string Key, string Label)[] MenuItemKeys =
    [
        ("translate", "翻译"), ("extract", "提取文字"), ("copy", "复制"), ("cut", "剪切"),
        ("paste", "粘贴"), ("saveas", "另存为"), ("reveal", "在文件夹中查看"),
        ("zoom50", "缩放为50%"), ("zoom100", "缩放为100%"), ("zoom150", "缩放为150%"),
        ("zoomout", "缩小"), ("zoomin", "扩大"), ("opacity", "透明度"), ("border", "边框"),
        ("imageproc", "图像处理"), ("annotate", "图片标注"), ("gallery_active", "参考图名单"),
        ("gallery_dust", "回收站"), ("gallery_clear", "清空回收站"), ("options", "选项"),
        ("destroy", "销毁"), ("closeall", "关闭所有贴图"), ("close", "关闭"),
    ];

    private void LoadUi()
    {
        // 常规
        PART_AutoStart.IsChecked = App.IsAutoStartEnabled();
        PART_RunAsAdmin.IsChecked = _editing.RunAsAdmin;
        PART_PersistPins.IsChecked = _editing.PersistPins;
        PART_ShowPanel.IsChecked = _editing.ShowPanelOnStartup;

        // 截图
        PART_HotkeyPin.Text = _editing.HotkeyPin;
        PART_HotkeyToggle.Text = _editing.HotkeyToggleVisibility;
        PART_CaptureCrosshair.IsChecked = _editing.CaptureCrosshair;
        PART_CaptureMagnifier.IsChecked = _editing.CaptureMagnifier;
        PART_CaptureIncludeCursor.IsChecked = _editing.CaptureIncludeCursor;
        // 截图后行为（互斥勾选）：转为贴图 / 只复制
        PART_CaptureAfterPin.IsChecked = _editing.CaptureAfterPin || !_editing.CaptureAfterCopy;
        PART_CaptureAfterCopy.IsChecked = _editing.CaptureAfterCopy && !_editing.CaptureAfterPin;
        PART_CaptureAfterPin.Checked += (_, _) => PART_CaptureAfterCopy.IsChecked = false;
        PART_CaptureAfterCopy.Checked += (_, _) => PART_CaptureAfterPin.IsChecked = false;
        PART_CompareWindow.IsChecked = _editing.TranslateShowCompareWindow;
        PART_CompareShowSource.IsChecked = _editing.CompareShowSource;
        PART_CompareShowTranslation.IsChecked = _editing.CompareShowTranslation;
        PART_TranslateOverlay.IsChecked = _editing.TranslateOverlay;

        // 贴图设置
        PART_DoubleClick.Items.Add(new ComboBoxItem { Content = "收缩为小图", Tag = "Compact" });
        PART_DoubleClick.Items.Add(new ComboBoxItem { Content = "关闭贴图", Tag = "Close" });
        PART_DoubleClick.SelectedIndex = _editing.DoubleClickAction.Equals("Close", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PART_DragSemiTransparent.IsChecked = _editing.DragSemiTransparent;
        PART_DefaultBorder.Items.Add(new ComboBoxItem { Content = "无边框", Tag = "None" });
        PART_DefaultBorder.Items.Add(new ComboBoxItem { Content = "单色边框", Tag = "Mono" });
        PART_DefaultBorder.SelectedIndex = _editing.DefaultBorderStyle == "Mono" ? 1 : 0;
        PART_MenuItems.Children.Clear();
        foreach (var (key, label) in MenuItemKeys)
        {
            var checkBox = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 0, 16, 12),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = key,
                IsChecked = !_editing.MenuVisibility.TryGetValue(key, out var visible) || visible,
            };
            PART_MenuItems.Children.Add(checkBox);
        }
        PART_DustboxCapacity.Text = Math.Clamp(_editing.DustboxCapacity, 1, 50).ToString();

        // 系统托盘菜单：单列 + 拖拽排序（顺序保存到 TrayOrder，托盘菜单按此顺序显示）
        PART_TrayItems.Items.Clear();
        // 排序：已保存顺序在前，未入序的新条目按默认顺序追加
        var orderedKeys = _editing.TrayOrder
            .Where(key => TrayItemKeys.Any(k => k.Key == key))
            .Concat(TrayItemKeys.Select(k => k.Key).Where(key => !_editing.TrayOrder.Contains(key)))
            .ToList();
        foreach (var key in orderedKeys)
        {
            var item = TrayItemKeys.First(k => k.Key == key);
            var row = new ListBoxItem
            {
                Content = new CheckBox
                {
                    Content = item.Label,
                    Margin = new Thickness(4, 2, 0, 2),
                    Tag = key,
                    IsChecked = !_editing.TrayVisibility.TryGetValue(key, out var visible) || visible,
                },
                Tag = key,
            };
            PART_TrayItems.Items.Add(row);
        }
        WireTrayDragReorder();
        PART_OcrUpscale.IsChecked = _editing.OcrUpscale;

        // 文字提取
        // 识别语言下拉 = 全部 5 种语言，直接可选；
        // 语言包由应用启动时自动批量安装（单次 UAC），无需手动安装入口
        PART_OcrLanguage.Items.Add(new ComboBoxItem { Content = "自动（跟随系统语言）", Tag = "" });
        foreach (var (tag, name) in OcrService.OcrLanguages)
            PART_OcrLanguage.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        var ocrIndex = 0;
        for (var i = 0; i < PART_OcrLanguage.Items.Count; i++)
        {
            if (((ComboBoxItem)PART_OcrLanguage.Items[i]).Tag as string == _editing.OcrLanguage)
            {
                ocrIndex = i;
                break;
            }
        }
        PART_OcrLanguage.SelectedIndex = ocrIndex;

        // 目标语言下拉
        foreach (var (name, code) in TargetLanguages)
            PART_TargetLang.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        // 翻译服务：引擎多选
        var freeEngines = _editing.FreeEngines.Count > 0
            ? _editing.FreeEngines.Select(e => e.ToLowerInvariant()).ToList()
            : ["youdao", "mymemory", "google"];
        PART_EngYoudao.IsChecked = freeEngines.Contains("youdao");
        PART_EngMymemory.IsChecked = freeEngines.Contains("mymemory");
        PART_EngGoogle.IsChecked = freeEngines.Contains("google");
        PART_EngBaidu.IsChecked = freeEngines.Contains("baidu");
        PART_EngCustom.IsChecked = freeEngines.Contains("custom");
        PART_EngOpenAI.IsChecked = _editing.TranslateProvider.Equals("openai", StringComparison.OrdinalIgnoreCase);
        PART_EngDeepL.IsChecked = _editing.TranslateProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase);
        PART_CustomUrl.Text = _editing.CustomUrl;
        PART_BaiduAppId.Text = _editing.BaiduAppId;
        PART_ApiKey.Text = _editing.FreeEngines.Contains("baidu")
            ? _editing.BaiduSecret
            : _editing.TranslateProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase)
                ? _editing.DeepL.AuthKey
                : _editing.OpenAI.ApiKey;
        PART_EngOpenAI.Checked += (_, _) => UpdateProviderVisibility();
        PART_EngOpenAI.Unchecked += (_, _) => UpdateProviderVisibility();
        PART_EngDeepL.Checked += (_, _) => UpdateProviderVisibility();
        PART_EngDeepL.Unchecked += (_, _) => UpdateProviderVisibility();
        PART_EngCustom.Checked += (_, _) => UpdateProviderVisibility();
        PART_EngCustom.Unchecked += (_, _) => UpdateProviderVisibility();
        PART_EngBaidu.Checked += (_, _) => UpdateProviderVisibility();
        PART_EngBaidu.Unchecked += (_, _) => UpdateProviderVisibility();
        PART_BaseUrl.Text = _editing.OpenAI.BaseUrl;
        PART_Model.Text = _editing.OpenAI.Model;
        var targetCode = _editing.TranslateProvider.ToLowerInvariant() switch
        {
            "openai" => NameToCode(_editing.OpenAI.TargetLang),
            "deepl" => _editing.DeepL.TargetLang,
            _ => _editing.FreeTargetLang,
        };
        SelectTargetLanguage(targetCode);
        PART_UseFreeApi.IsChecked = _editing.DeepL.UseFreeApi;
        UpdateProviderVisibility();
    }

    private void UpdateProviderVisibility()
    {
        var isOpenAi = PART_EngOpenAI.IsChecked == true;
        var isDeepL = PART_EngDeepL.IsChecked == true;
        var isBaidu = PART_EngBaidu.IsChecked == true;
        var isCustom = PART_EngCustom.IsChecked == true;
        PART_BaseUrlRow.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        PART_ModelRow.Visibility = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        PART_ApiKeyRow.Visibility = isOpenAi || isDeepL || isBaidu ? Visibility.Visible : Visibility.Collapsed;
        PART_ApiKeyLabel.Text = isDeepL ? "DeepL 密钥" : isBaidu ? "百度密钥" : "API Key";
        PART_UseFreeApi.Visibility = isDeepL ? Visibility.Visible : Visibility.Collapsed;
        PART_CustomUrlRow.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        PART_BaiduAppIdRow.Visibility = isBaidu ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>托盘条目拖拽排序：按住条目上下拖动调整顺序（顺序随保存写入 TrayOrder）。</summary>
    private void WireTrayDragReorder()
    {
        ListBoxItem? dragging = null;
        PART_TrayItems.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (PART_TrayItems.SelectedItem is ListBoxItem { Tag: not null } selected)
                dragging = selected;
        };
        PART_TrayItems.PreviewMouseMove += (_, e) =>
        {
            if (dragging == null || e.LeftButton != MouseButtonState.Pressed)
                return;
            var target = GetRowUnder(e.GetPosition(PART_TrayItems));
            if (target == null || ReferenceEquals(target, dragging))
                return;
            var newIndex = Math.Max(0, PART_TrayItems.Items.IndexOf(target));
            var oldIndex = PART_TrayItems.Items.IndexOf(dragging);
            if (newIndex == oldIndex)
                return;
            PART_TrayItems.Items.Remove(dragging);
            PART_TrayItems.Items.Insert(newIndex, dragging);
            PART_TrayItems.SelectedItem = dragging;
        };
        PART_TrayItems.PreviewMouseLeftButtonUp += (_, _) => dragging = null;
    }

    private ListBoxItem? GetRowUnder(Point position)
    {
        var element = PART_TrayItems.InputHitTest(position) as System.Windows.Media.Visual;
        while (element != null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element) as System.Windows.Media.Visual;
        return element as ListBoxItem;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var error = ConfigStore.ValidateHotkey(PART_HotkeyPin.Text)
            ?? ConfigStore.ValidateHotkey(PART_HotkeyToggle.Text);
        if (error != null)
        {
            MessageBox.Show(this, error, "热键无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(PART_HotkeyPin.Text))
        {
            MessageBox.Show(this, "截图贴图热键不能为空。", "热键无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 常规
        var adminChanged = PART_RunAsAdmin.IsChecked == true != _editing.RunAsAdmin;
        if (PART_AutoStart.IsChecked == true != App.IsAutoStartEnabled())
            App.SetAutoStart(PART_AutoStart.IsChecked == true);
        _editing.RunAsAdmin = PART_RunAsAdmin.IsChecked == true;
        _editing.PersistPins = PART_PersistPins.IsChecked == true;
        _editing.ShowPanelOnStartup = PART_ShowPanel.IsChecked == true;

        // 截图
        _editing.HotkeyPin = PART_HotkeyPin.Text.Trim();
        _editing.HotkeyToggleVisibility = PART_HotkeyToggle.Text.Trim();
        _editing.CaptureCrosshair = PART_CaptureCrosshair.IsChecked == true;
        _editing.CaptureMagnifier = PART_CaptureMagnifier.IsChecked == true;
        _editing.CaptureIncludeCursor = PART_CaptureIncludeCursor.IsChecked == true;
        _editing.CaptureAfterPin = PART_CaptureAfterPin.IsChecked == true;
        _editing.CaptureAfterCopy = PART_CaptureAfterCopy.IsChecked == true;
        if (!_editing.CaptureAfterPin && !_editing.CaptureAfterCopy)
            _editing.CaptureAfterPin = true; // 都不勾 → 按转为贴图处理
        _editing.TranslateShowCompareWindow = PART_CompareWindow.IsChecked == true;
        _editing.CompareShowSource = PART_CompareShowSource.IsChecked == true;
        _editing.CompareShowTranslation = PART_CompareShowTranslation.IsChecked == true;
        _editing.TranslateOverlay = PART_TranslateOverlay.IsChecked == true;

        // 参考图
        _editing.DoubleClickAction = (PART_DoubleClick.SelectedItem as ComboBoxItem)?.Tag as string ?? "Compact";
        _editing.DragSemiTransparent = PART_DragSemiTransparent.IsChecked == true;
        _editing.DefaultBorderStyle = (PART_DefaultBorder.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";
        _editing.MenuVisibility = [];
        foreach (var checkBox in PART_MenuItems.Children.OfType<CheckBox>())
            _editing.MenuVisibility[(string)checkBox.Tag!] = checkBox.IsChecked != false;
        _editing.DustboxCapacity = int.TryParse(PART_DustboxCapacity.Text.Trim(), out var dustbox)
            ? Math.Clamp(dustbox, 1, 50)
            : _editing.DustboxCapacity;
        _editing.TrayVisibility = [];
        _editing.TrayOrder = [];
        foreach (var row in PART_TrayItems.Items.OfType<ListBoxItem>())
        {
            var checkBox = (CheckBox)row.Content;
            var key = (string)checkBox.Tag!;
            _editing.TrayVisibility[key] = checkBox.IsChecked != false;
            _editing.TrayOrder.Add(key);
        }
        _editing.OcrUpscale = PART_OcrUpscale.IsChecked == true;

        // 文字提取
        _editing.OcrLanguage = (PART_OcrLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        // 翻译服务：引擎勾选 → FreeEngines（固定顺序：有道→MyMemory→Google）+ 高级接口
        _editing.FreeEngines = [];
        if (PART_EngYoudao.IsChecked == true) _editing.FreeEngines.Add("youdao");
        if (PART_EngMymemory.IsChecked == true) _editing.FreeEngines.Add("mymemory");
        if (PART_EngGoogle.IsChecked == true) _editing.FreeEngines.Add("google");
        if (PART_EngBaidu.IsChecked == true) _editing.FreeEngines.Add("baidu");
        if (PART_EngCustom.IsChecked == true) _editing.FreeEngines.Add("custom");
        _editing.CustomUrl = PART_CustomUrl.Text.Trim();

        var targetCode = (PART_TargetLang.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
        _editing.FreeTargetLang = targetCode;
        if (PART_EngOpenAI.IsChecked == true)
        {
            _editing.TranslateProvider = "openai";
            _editing.OpenAI.BaseUrl = string.IsNullOrWhiteSpace(PART_BaseUrl.Text) ? "https://api.openai.com/v1" : PART_BaseUrl.Text.Trim();
            _editing.OpenAI.Model = string.IsNullOrWhiteSpace(PART_Model.Text) ? "gpt-4o-mini" : PART_Model.Text.Trim();
            _editing.OpenAI.TargetLang = CodeToName(targetCode);
            _editing.OpenAI.ApiKey = PART_ApiKey.Text.Trim();
        }
        else if (PART_EngDeepL.IsChecked == true)
        {
            _editing.TranslateProvider = "deepl";
            _editing.DeepL.AuthKey = PART_ApiKey.Text.Trim();
            _editing.DeepL.TargetLang = targetCode.ToUpperInvariant();
            _editing.DeepL.UseFreeApi = PART_UseFreeApi.IsChecked == true;
        }
        else
        {
            _editing.TranslateProvider = "auto";
        }

        // 百度引擎字段（勾选百度引擎时启用）
        if (_editing.FreeEngines.Contains("baidu"))
        {
            _editing.BaiduAppId = PART_BaiduAppId.Text.Trim();
            _editing.BaiduSecret = PART_ApiKey.Text.Trim();
        }

        ConfigStore.Save(_editing);
        App.ApplyConfig(_editing);
        Close();

        // 管理员选项变化时提示立即重启生效
        if (adminChanged && _editing.RunAsAdmin)
        {
            var choice = MessageBox.Show(this,
                "已开启“以管理员身份运行”。是否立即以管理员权限重启 Pinshot？",
                "Pinshot", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) &&
                    Core.Win32.ShellExecute(nint.Zero, "runas", exePath, null, null, 5) > 32)
                    Application.Current.Shutdown();
            }
        }
    }

    private static readonly (string Name, string Code)[] TargetLanguages =
    [
        ("中文（简体）", "zh-CN"), ("English", "en"), ("日本語", "ja"),
        ("한국어", "ko"), ("Русский", "ru"),
    ];

    private void SelectTargetLanguage(string code)
    {
        for (var i = 0; i < PART_TargetLang.Items.Count; i++)
        {
            if (!string.Equals(((ComboBoxItem)PART_TargetLang.Items[i]).Tag as string, code,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            PART_TargetLang.SelectedIndex = i;
            return;
        }
        if (PART_TargetLang.Items.Count > 0)
            PART_TargetLang.SelectedIndex = 0;
    }

    private static string CodeToName(string code) => TargetLanguages
        .FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).Name ?? "中文（简体）";

    private static string NameToCode(string name) => TargetLanguages
        .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).Code
        ?? (string.IsNullOrWhiteSpace(name) ? "zh-CN" : name);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
