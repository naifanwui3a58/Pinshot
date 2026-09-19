using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Pinshot.Core;
using Pinshot.Views;

namespace Pinshot;

/// <summary>
/// Pinshot：Setuna 式截图贴图工具 + 文字提取/翻译。
/// 无主窗口，启动后仅驻留托盘；贴图窗口即产品本体。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Pinshot.SingleInstance.2F6C9A41-8D3E-4B7A-9C15-E0A82D4F7B33";
    private const string AutoRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private Mutex? _mutex;
    private static Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _trayIcon;
    private static MenuItem? _persistMenuItem;

    public static Config Config { get; private set; } = new();

    public static PinManager Pins { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常记录：托盘应用不能静默闪退，任何未处理异常都要留下现场
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomainUnhandledException", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // 单实例锁：等待重试（提权重启时旧实例退出与善后有几秒竞态，直接判 false 会误杀新实例）
        _mutex = new Mutex(false, MutexName, out _);
        var acquired = false;
        for (var attempt = 0; attempt < 15 && !acquired; attempt++)
        {
            try
            {
                acquired = _mutex.WaitOne(600);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // 前任持有者异常退出，锁已由本实例接管
            }
        }
        if (!acquired)
        {
            // 确有健康实例在运行：静默退出，并通知其弹出主面板
            try
            {
                using var signal = EventWaitHandle.OpenExisting(MutexName + ".ShowPanel");
                signal.Set();
            }
            catch
            {
                // 信号量不存在：直接退出
            }
            Shutdown();
            return;
        }

        // 监听“再启动一次”信号：弹出主面板
        var panelSignal = new EventWaitHandle(false, EventResetMode.AutoReset, MutexName + ".ShowPanel");
        ThreadPool.RegisterWaitForSingleObject(
            panelSignal,
            (_, _) => Dispatcher.Invoke(Views.PanelWindow.ShowSingle),
            null,
            -1,
            executeOnlyOnce: false);

        Config = ConfigStore.Load();
        TryElevateIfNeeded(e.Args);
        RegisterHotkeys();
        InitTray();

        // Setuna 行为：重启后恢复未关闭的贴图
        if (Config.PersistPins)
            Pins.RestoreAll();

        // PaddleOCR 单文件发布：native 依赖解压在 AppContext.BaseDirectory（临时解包目录），
        // Paddle 按 PATH 搜索依赖链（mklml.dll 等），不把解包目录加进 PATH 会加载失败
        var dirsToAdd = new List<string> { AppContext.BaseDirectory };
        // 单文件 bundle 的 native 库解压在 %TEMP%\.net\Pinshot\<hash>\，同样要进搜索路径
        var bundleRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".net", "Pinshot");
        if (System.IO.Directory.Exists(bundleRoot))
            dirsToAdd.AddRange(System.IO.Directory.GetDirectories(bundleRoot));
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in dirsToAdd.AsEnumerable().Reverse())
        {
            if (!pathVar.Contains(dir, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", dir + ";" + pathVar);
        }

        // 托盘勾选生效实测：隐藏“截图”→ 重建菜单 → 数条目 → 恢复
        var trayAll = _trayIcon.ContextMenu.Items.Count;
        Config.TrayVisibility["capture"] = false;
        ApplyConfig(Config);
        var trayHidden = _trayIcon.ContextMenu.Items.Count;
        Config.TrayVisibility["capture"] = true;
        ApplyConfig(Config);
        var trayRestored = _trayIcon.ContextMenu.Items.Count;
        LogCrash("SelfTest", new Exception(
            $"托盘勾选实测：全部={trayAll} → 隐藏截图后={trayHidden} → 恢复后={trayRestored}"));

        // PaddleOCR：先释放内嵌模型与 native 依赖并预加载，再后台预热（首次初始化 1-3 秒）
        Core.PaddleModels.EnsureExtracted();
        Core.NativeLoader.ExtractAndPreload();
        Core.PaddleOcrEngine.Warmup();

        // OCR 语言包：未装齐时后台批量安装（单次 UAC 弹窗），装完后记录标记不再重复
        if (!Config.OcrPacksInstalled)
            InstallMissingOcrPacksInBackground();

        // 自检模式：验证截图链路可创建/可取消，不进入交互
        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunSelfTestAsync();
            return;
        }

        // 启动面板（Setuna 式主窗口：截取/翻译/选项）
        if (Config.ShowPanelOnStartup)
            Views.PanelWindow.ShowSingle();
    }

    /// <summary>
    /// 自检：跑一遍截图链路（创建覆盖层 → 自动取消 → 等待协作状态复位），
    /// 结果写入 crash.log，用于无交互环境下定位崩溃。
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        try
        {
            // 第 -2 步：单键热键实测（注册 D1 → 模拟真实按下 → 验证触发）
            var hotkeyFired = false;
            using (var testHotkeys = new HotkeyService())
            {
                testHotkeys.Register("test.d1", Key.D1, ModifierKeys.None, () => hotkeyFired = true);
                await Task.Delay(300);
                Core.Win32.keybd_event(0x31, 0, 0, nint.Zero); // '1' 按下
                Core.Win32.keybd_event(0x31, 0, 2, nint.Zero); // '1' 释放
                await Task.Delay(600);
            }
            LogCrash("SelfTest", new Exception($"单键热键 D1 实测：{(hotkeyFired ? "触发成功 ✔" : "未触发 ✘")}"));

            // 第 -1 步：PaddleOCR 实测（画图含中文/数字/英文，验证识别质量）
            try
            {
                using var probe = new System.Drawing.Bitmap(560, 120, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(probe))
                {
                    g.Clear(System.Drawing.Color.White);
                    using var font = new System.Drawing.Font("Microsoft YaHei", 20, System.Drawing.FontStyle.Regular);
                    g.DrawString("订单号 20240815 共 200 元，Please pay in 3 days.", font, System.Drawing.Brushes.Black, 12, 40);
                }
                var ocr = await Core.OcrService.RecognizeAsync(probe, App.Config.OcrLanguage);
                LogCrash("SelfTest", new Exception($"OCR 引擎={ocr.LanguageTag} 识别=[{ocr.Text.Replace(System.Environment.NewLine, " | ")}] 行数={ocr.Lines.Count}"));
            }
            catch (Exception ex)
            {
                LogCrash("SelfTest", new Exception($"OCR 实测失败：{ex.Message}"));
            }

            // 第 0 步：翻译链路（免费接口链自动回退）
            try
            {
                var translated = await TranslateService.TranslateAsync(
                    "This asset consists of a single Blender file with just over 200 free props.", App.Config);
                LogCrash("SelfTest", new Exception($"翻译链路通过：{translated}"));

// 在自检里追加：中英混合分段实测
var mixed = "这是一款优雅到极致的桌面宠物应用，支持 Gpet 和 Steam 平台。";
var seg = TranslateService.SplitByScript(mixed, targetChinese: true);
LogCrash("SelfTest", new Exception(
    "混合分段：共 " + seg.Count + " 段 —— " +
    string.Join(" | ", seg.Parts.Select(p =>
        (p.Keep ? "[保留" : "[翻译") + p.Text + "]"))));

            }
            catch (Exception ex)
            {
                LogCrash("SelfTest", new Exception($"翻译链路失败：{ex.Message}"));
            }

            // 窗口自动检测诊断：枚举候选窗口 + 模拟鼠标悬停看吸附结果
            var hits = Win32.EnumWindowBounds();
            LogCrash("SelfTest", new Exception($"窗口自动检测：候选 {hits.Count} 个" +
                Environment.NewLine + string.Join(Environment.NewLine,
                    hits.Take(5).Select(h => "  " + h.Bounds))));

            LogCrash("SelfTest", new Exception("自检开始：取消路径"));
            var task = CaptureService.CaptureAsync();
            await Task.Delay(800);
            // 把鼠标移到屏幕中心（那里通常是某个窗口）再读取吸附结果
            var areas = Win32.GetMonitorWorkAreasFull();
            if (areas.Count > 0)
            {
                var monitor = areas[0];
                Win32.SetCursorPos(monitor.Left + monitor.Width / 2, monitor.Top + monitor.Height / 2);
            }
            await Task.Delay(600);
            LogCrash("SelfTest", new Exception("悬停吸附：" + Views.CaptureOverlayWindow.DescribeHoverForSelfTest()));
            // UIA 元素级检测探针（浏览器网页/应用内部元素）
            try
            {
                var areas2 = Win32.GetMonitorWorkAreasFull();
                if (areas2.Count > 0)
                {
                    var m0 = areas2[0];
                    Win32.SetCursorPos(m0.Left + m0.Width / 3, m0.Top + m0.Height / 2);
                    await Task.Delay(600);
                    LogCrash("SelfTest", "UIA 探针1：" + Views.CaptureOverlayWindow.DescribeHoverForSelfTest());
                    Win32.SetCursorPos(m0.Left + m0.Width * 2 / 3, m0.Top + m0.Height / 2);
                    await Task.Delay(600);
                    LogCrash("SelfTest", "UIA 探针2：" + Views.CaptureOverlayWindow.DescribeHoverForSelfTest());
                }
            }
            catch (Exception ex)
            {
                LogCrash("SelfTest", new Exception($"UIA 探针失败：{ex.Message}"));
            }
            Views.CaptureOverlayWindow.CancelForSelfTest();
            var cancelled = await task;
            LogCrash("SelfTest", new Exception($"取消路径结束：result={(cancelled == null ? "null(已取消)" : cancelled.PhysicalBounds.ToString())}"));

            // 第二步：完整路径 —— 框选一块区域 → 生成贴图 → 关闭贴图
            LogCrash("SelfTest", new Exception("自检开始：框选+建贴图路径"));
            var task2 = CaptureService.CaptureAsync();
            await Task.Delay(1200);
            Views.CaptureOverlayWindow.SelectForSelfTest(new System.Drawing.Rectangle(120, 120, 480, 320));
            var selected = await task2;
            if (selected == null)
            {
                LogCrash("SelfTest", new Exception("框选路径失败：未返回选区"));
            }
            else
            {
                LogCrash("SelfTest", new Exception($"框选成功：{selected.PhysicalBounds}，位图 {selected.Bitmap.Width}x{selected.Bitmap.Height}"));
                var pin = Pins.CreatePin(selected.Bitmap, selected.PhysicalBounds);
                // 顺带验证图上原位翻译（不再开窗口）
                await pin.TranslateAsync();
                LogCrash("SelfTest", new Exception("图上原位翻译执行完毕"));
                // 顺带验证提取文字面板
                pin.ExtractText();
                await Task.Delay(600);
                await Task.Delay(800);

                // 回归实测：大图（短边≥1000 → OCR 走原图路径）连续两次翻译。
                // 旧版缺陷：OCR 的 using 误把贴图位图一起 Dispose，第二次抛“Parameter is not valid.”
                try
                {
                    var bigProbe = new System.Drawing.Bitmap(1300, 1040, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(bigProbe))
                    {
                        g.Clear(System.Drawing.Color.White);
                        using var font = new System.Drawing.Font("Microsoft YaHei", 20, System.Drawing.FontStyle.Regular);
                        g.DrawString("Regression 2024 large image", font, System.Drawing.Brushes.Black, 12, 40);
                    }
                    var bigPin = Pins.CreatePin(bigProbe, new System.Drawing.Rectangle(300, 640, 1300, 1040));
                    await bigPin.TranslateAsync();
                    await bigPin.TranslateAsync(); // 旧版在此抛异常
                    LogCrash("SelfTest", new Exception("大图连翻两次回归：通过（贴图位图未被 OCR 释放）"));
                    bigPin.Close();
                }
                catch (Exception ex)
                {
                    LogCrash("SelfTest", new Exception($"大图连翻两次回归：失败 {ex.Message}"));
                }

                // UI 冒烟：逐个创建各窗口，验证主题资源与 XAML 均可正常解析
                var smoke = new List<string>();
                Smoke(smoke, "图片标注工具条", () =>
                {
                    var toolbar = new Views.AnnotationToolbarWindow(pin);
                    toolbar.Show();
                    toolbar.Close();
                });
                Smoke(smoke, "提取文字面板", () => Views.ExtractPanelWindow.Show(pin, "第一段文字" + Environment.NewLine + "Second paragraph"));
                Smoke(smoke, "结果窗口", () => Views.ResultWindow.Show(pin, "测试标题", "测试内容"));
                Smoke(smoke, "参考图名单", Views.PinGalleryWindow.ShowActive);
                Smoke(smoke, "回收站", Views.PinGalleryWindow.ShowDustbox);
                Smoke(smoke, "设置窗口", Views.SettingsWindow.ShowSingle);
                Smoke(smoke, "主面板", Views.PanelWindow.ShowSingle);

                // 视觉自检：逐窗截图存 PNG，用于人工核对 UI（不再只看编译绿勾）
                // （置顶贴图在 extract/toolbar 截图后再关闭，避免遮挡）
                var shots = new List<string>();
                // 翻译中 loading：贴图遮罩 + 旋转圆弧实测截图
                pin.ShowTranslateLoading(true);
                Shot(shots, pin, "loading", null, closeAfter: false);
                pin.ShowTranslateLoading(false);
                Shot(shots, pin, "pin", null, closeAfter: false);
                Shot(shots, null, "extract", () => Views.ExtractPanelWindow.Show(pin, "第一行测试文本。" + Environment.NewLine + "Second line for wrap check, enough length to wrap into two lines for real layout check."), closeAfter: true);
                // 对照翻译窗口：长内容高度自适应实测（两框按内容比例分高度、各自滚动）
                Shot(shots, null, "translate", () => Views.TranslateWindow.Show(pin,
                    string.Join(Environment.NewLine, Enumerable.Range(1, 30).Select(i => $"Node2D CanvasItem {i}")),
                    string.Join(Environment.NewLine, Enumerable.Range(1, 30).Select(i => $"节点二维 画布项 {i} 高度自适应测试"))), closeAfter: true);
                Shot(shots, null, "toolbar", () => pin.OpenToolbarForTest(), closeAfter: true);
                // 关闭置顶贴图后再截 panel/settings，避免遮挡
                pin.Close();
                await Task.Delay(400);
                Shot(shots, null, "panel", Views.PanelWindow.ShowSingle, closeAfter: true);
                Shot(shots, null, "settings", Views.SettingsWindow.ShowSingle, closeAfter: true);
                // 翻译与提取文字页（OCR 语言下拉页）截图
                Shot(shots, null, "ocrpage", () =>
                {
                    Views.SettingsWindow.ShowSingle();
                    var settings = Application.Current.Windows.OfType<Views.SettingsWindow>().First();
                    var navField = typeof(Views.SettingsWindow)
                        .GetField("PART_Nav", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (navField?.GetValue(settings) is System.Windows.Controls.ListBox nav)
                        nav.SelectedIndex = 3;
                }, closeAfter: true);
                // 系统托盘菜单页截图（验证单列 + 勾选）
                Shot(shots, null, "traypage", () =>
                {
                    Views.SettingsWindow.ShowSingle();
                    var settings = Application.Current.Windows.OfType<Views.SettingsWindow>().First();
                    var navField = typeof(Views.SettingsWindow)
                        .GetField("PART_Nav", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (navField?.GetValue(settings) is System.Windows.Controls.ListBox nav)
                        nav.SelectedIndex = 4;
                }, closeAfter: true);
                LogCrash("SelfTest", new Exception("视觉自检截图：" + Environment.NewLine + string.Join(Environment.NewLine, shots)));

                await Task.Delay(800);
                foreach (var window in Application.Current.Windows.OfType<Window>().ToArray())
                {
                    if (!ReferenceEquals(window, pin))
                        window.Close();
                }
                pin.Close();
                await Task.Delay(500);
                LogCrash("SelfTest", new Exception("全部窗口已关闭，完整路径 + UI 冒烟通过"));
            }
        }
        catch (Exception ex)
        {
            LogCrash("SelfTest失败", ex);
        }
        Shutdown();
    }

    private static void Shot(List<string> results, Window? owner, string name, Action? open = null, bool closeAfter = true)
    {
        try
        {
            open?.Invoke();
            // 按窗口类型精确匹配（避免抓错成别的弹窗）
            var typeName = name switch
            {
                "extract" => "ExtractPanelWindow",
                "translate" => "TranslateWindow",
                "toolbar" => "AnnotationToolbarWindow",
                "panel" => "PanelWindow",
                "settings" or "traypage" or "ocrpage" => "SettingsWindow",
                _ => "",
            };
            Window? window = owner ?? Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.GetType().Name == typeName && w.IsVisible);
            if (window == null)
            {
                results.Add($"  ✘ {name}：窗口未找到（{typeName}）");
                return;
            }
            if (window == null)
            {
                results.Add($"  ✘ {name}：窗口未找到");
                return;
            }
            window.Left = 80;
            window.Top = 80;
            System.Windows.Input.Keyboard.ClearFocus();
            Application.Current.Dispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
            Thread.Sleep(250);
            window.InvalidateVisual();
            Application.Current.Dispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));
            Thread.Sleep(150);

            var dpi = 1.0;
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
                dpi = source.CompositionTarget.TransformToDevice.M11;
            var width = (int)(window.ActualWidth * dpi);
            var height = (int)(window.ActualHeight * dpi);
            if (width <= 0 || height <= 0)
            {
                results.Add($"  ✘ {name}：尺寸异常 {width}x{height}");
                return;
            }
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(
                    (int)(window.Left * dpi), (int)(window.Top * dpi), 0, 0,
                    new System.Drawing.Size(width, height));
            }
            var dir = System.IO.Path.Combine(ConfigStore.DirectoryPath, "ui");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, name + ".png");
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            if (closeAfter && !ReferenceEquals(window, owner))
                window.Close();
            results.Add($"  ✔ {name} → {file}");
        }
        catch (Exception ex)
        {
            results.Add($"  ✘ {name}：{ex.Message}");
        }
    }

    private static void Smoke(List<string> results, string name, Action action)
    {
        try
        {
            action();
            results.Add($"  ✔ {name}");
        }
        catch (Exception ex)
        {
            results.Add($"  ✘ {name}：{ex.GetType().Name} {ex.Message}");
            LogCrash($"UI冒烟-{name}", ex);
        }
    }

    private static void LogCrash(string source, string message) =>
        LogCrash(source, new Exception(message));

    internal static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var path = System.IO.Path.Combine(ConfigStore.DirectoryPath, "crash.log");
            System.IO.Directory.CreateDirectory(ConfigStore.DirectoryPath);
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                       (exception?.ToString() ?? "(无异常对象)") + Environment.NewLine + new string('-', 60) + Environment.NewLine;
            System.IO.File.AppendAllText(path, text);
        }
        catch
        {
            // 日志失败不能再抛
        }
    }

    /// <summary>以管理员身份运行选项：未提权且配置开启时，通过 Shell runas 动词自提升重启。</summary>
    private void TryElevateIfNeeded(string[] args)
    {
        // 提权重启带来的子实例已是管理员，不再二次提升
        if (args.Contains("--from-elevation", StringComparer.OrdinalIgnoreCase))
            return;
        if (!Config.RunAsAdmin || IsElevated())
            return;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;
        // 转发启动参数（如 --selftest）给提权后的新实例
        var forwarded = args.Length == 0 ? "" : string.Join(" ", args);
        // ShellExecute 的 runas 动词触发 UAC；返回值 > 32 表示成功
        if (Core.Win32.ShellExecute(nint.Zero, "runas", exePath,
                forwarded.Length == 0 ? null : forwarded, null, 5 /* SW_SHOW */) > 32)
            Shutdown();
        // 用户取消 UAC 则以普通权限继续运行
    }

    internal static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    internal static void ApplyConfig(Config config)
    {
        Config = config;
        RegisterHotkeys();
        // 托盘勾选变化立即生效：在 UI 线程重建托盘菜单
        Current?.Dispatcher.Invoke(RebuildTrayMenu);
    }

    internal static void RegisterHotkeys()
    {
        Register("Pinshot.Pin", Config.HotkeyPin, () => _ = CapturePinAsync());
        Register("Pinshot.ToggleVisibility", Config.HotkeyToggleVisibility, Pins.ToggleVisibility);
    }

    public static HotkeyService Hotkeys { get; } = new();

    private static void Register(string name, string gestureText, Action action)
    {
        if (string.IsNullOrWhiteSpace(gestureText))
            return;
        // 手动解析 "Ctrl+Alt+P" / "D1" / "F5" 等格式——支持任意单键作全局热键
        Key key;
        var modifiers = ModifierKeys.None;
        try
        {
            var parts = gestureText.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;
            key = Enum.Parse<Key>(parts[^1], ignoreCase: false);
            foreach (var modifier in parts[..^1])
            {
                modifiers |= modifier.ToLowerInvariant() switch
                {
                    "ctrl" or "control" => ModifierKeys.Control,
                    "alt" => ModifierKeys.Alt,
                    "shift" => ModifierKeys.Shift,
                    "win" or "windows" => ModifierKeys.Windows,
                    _ => throw new FormatException(modifier),
                };
            }
        }
        catch
        {
            // 非法热键字符串直接忽略，保留当前注册状态
            return;
        }
        Hotkeys.Register(name, key, modifiers, action);
    }

    /// <summary>截图（SETUNA 式：框选/点窗口松手即出贴图）。</summary>
    internal static async Task CapturePinAsync(bool hidePanelDuringCapture = false)
    {
        var panel = hidePanelDuringCapture ? Views.PanelWindow.VisibleInstance : null;
        panel?.Hide();
        try
        {
            var result = await CaptureService.CaptureAsync();
            if (result == null)
                return;
            Pins.CreatePin(result.Bitmap, result.PhysicalBounds);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"截图失败：{ex.Message}", "Pinshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            panel?.Show();
        }
    }

    /// <summary>截图 → 提取文字。</summary>
    internal static async Task CaptureExtractAsync()
    {
        var result = await CaptureService.CaptureAsync();
        if (result == null)
            return;
        var pin = Pins.CreatePin(result.Bitmap, result.PhysicalBounds);
        pin.ExtractText();
    }

    /// <summary>
    /// 一次性把全部 5 种 OCR 语言包装齐：检查缺失 → 单次 UAC 提权 → PowerShell 批量
    /// Add-WindowsCapability。语言清单来自固定白名单（OcrService.OcrLanguages），
    /// 不含用户自由输入。装完（或启动过安装）后记录标记，不再重复提权。
    /// </summary>
    private void InstallMissingOcrPacksInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var installed = OcrService.GetAvailableLanguages()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = OcrService.OcrLanguages
                    .Select(l => l.Tag)
                    .Where(tag => !installed.Contains(tag))
                    .ToList();
                if (missing.Count == 0)
                {
                    Config.OcrPacksInstalled = true;
                    ConfigStore.Save(Config);
                    return;
                }

                var commands = string.Join("; ",
                    missing.Select(tag =>
                        "Add-WindowsCapability -Online -Name 'Language.OCR~~~"
                        + tag + "~0.0.1.0'"));
                var script = commands
                    + "; Write-Host 'OCR 语言包安装完成'; Read-Host '按回车关闭'";

                // 单次 UAC 提权执行全部缺失项；返回值 > 32 表示已成功弹出并启动
                var launch = Core.Win32.ShellExecute(
                    nint.Zero, "runas", "powershell.exe",
                    "-NoProfile -Command \"" + script + "\"", null, 5 /* SW_SHOW */);
                if (launch > 32)
                {
                    Config.OcrPacksInstalled = true;
                    ConfigStore.Save(Config);
                }
                // 用户取消 UAC：不记标记，下次启动再次尝试
            }
            catch
            {
                // 检测失败不影响主流程
            }
        });
    }

    /// <summary>面板“翻译”按钮：截图后直接进入图上原位翻译。</summary>
    internal static async Task CaptureTranslateAsync()
    {
        var panel = Views.PanelWindow.VisibleInstance;
        panel?.Hide();
        try
        {
            var result = await CaptureService.CaptureAsync();
            if (result == null)
                return;
            var pin = Pins.CreatePin(result.Bitmap, result.PhysicalBounds);
            await pin.TranslateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"截图翻译失败：{ex.Message}", "Pinshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            panel?.Show();
        }
    }

    private void InitTray()
    {
        var buildTime = Environment.ProcessPath is { } path
            ? File.GetLastWriteTime(path)
            : DateTime.Now;
        _trayIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
        {
            ToolTipText = $"Pinshot 截图贴图（build {buildTime:MM-dd HH:mm}）",
            IconSource = LoadAppIcon(),
        };
        RebuildTrayMenu();
        // 托盘：不监听任何左键事件（Hardcodet 左键事件在右键交互时也会触发导致误弹面板），
        // 主面板只能从右键菜单打开
    }

    /// <summary>按当前配置重建托盘右键菜单（设置勾选变化后调用，立即生效）。</summary>
    private static void RebuildTrayMenu()
    {
        if (_trayIcon == null)
            return;
        var menu = new ContextMenu();

        // 托盘条目统一入口：按“设置 → 系统托盘”的勾选显隐
        void AddItem(string key, string header, Action action)
        {
            if (Config.TrayVisibility.TryGetValue(key, out var visible) && !visible)
                return;
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // ===== 托盘功能 =====
        // 条目顺序按“设置 → 系统托盘菜单”里用户拖拽的顺序（TrayOrder）显示；
        // 未入序的新条目追加在末尾。每个条目单独受勾选控制。
        var orderedKeys = Config.TrayOrder.Count > 0
            ? Config.TrayOrder
                .Concat(TrayDefaultOrder.Where(k => !Config.TrayOrder.Contains(k)))
                .ToList()
            : new List<string>(TrayDefaultOrder);

        foreach (var key in orderedKeys)
        {
            if (Config.TrayVisibility.TryGetValue(key, out var visible) && !visible)
                continue;

            switch (key)
            {
                case "capture":
                    AddItem(key, "截图", () => _ = CapturePinAsync());
                    break;
                case "translate":
                    AddItem(key, "翻译", () => _ = CaptureTranslateAsync());
                    break;
                case "extract":
                    AddItem(key, "提取文字", () => _ = CaptureExtractAsync());
                    break;
                case "panel":
                    AddItem(key, "主面板", Views.PanelWindow.ShowSingle);
                    break;
                case "fromfile":
                    AddItem(key, "从文件创建贴图...", SelectFilesAndPin);
                    break;
                case "toggle":
                    AddItem(key, "显示/隐藏所有贴图", Pins.ToggleVisibility);
                    break;
                case "activate":
                    AddItem(key, "激活所有贴图", Pins.ActivateAll);
                    break;
                case "closeall":
                    AddItem(key, "关闭所有贴图", Pins.CloseAll);
                    break;
                case "gallery_active":
                    AddItem(key, "参考图名单", Views.PinGalleryWindow.ShowActive);
                    break;
                case "gallery_dust":
                    AddItem(key, "回收站", Views.PinGalleryWindow.ShowDustbox);
                    break;
                case "gallery_clear":
                    AddItem(key, "清空回收站", Pins.ClearDustbox);
                    break;
                case "options":
                    AddItem(key, "选项...", SettingsWindow.ShowSingle);
                    break;
                case "persist":
                    AddItem(key, "重启后恢复贴图", () =>
                    {
                        Config.PersistPins = !Config.PersistPins;
                        ConfigStore.Save(Config);
                        if (!Config.PersistPins)
                            Pins.SaveAllNow(); // 关闭恢复时立即清掉落盘数据
                    });
                    break;
                case "autostart":
                    AddItem(key, "开机自启动", () =>
                    {
                        var enabled = IsAutoStartEnabled();
                        SetAutoStart(!enabled);
                        MessageBox.Show(!enabled ? "已开启开机自启动。" : "已关闭开机自启动。", "Pinshot",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                    break;
                case "configdir":
                    AddItem(key, "打开配置文件夹", () =>
                        System.Diagnostics.Process.Start("explorer.exe", ConfigStore.DirectoryPath));
                    break;
                case "exit":
                    AddSep();
                    AddItem(key, "退出", () => Current.Shutdown());
                    break;
            }
        }

        void AddSep()
        {
            if (menu.Items.Count == 0 || menu.Items[^1] is Separator)
                return;
            menu.Items.Add(new Separator());
        }

        _trayIcon.ContextMenu = menu;
    }

    /// <summary>托盘菜单默认顺序（用户未排序或出现新条目时追加）。</summary>
    private static readonly string[] TrayDefaultOrder =
    [
        "capture", "translate", "extract", "panel", "fromfile", "toggle", "activate",
        "closeall", "gallery_active", "gallery_dust", "gallery_clear", "options", "persist", "autostart",
        "configdir", "exit",
    ];



    private static void SelectFilesAndPin()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片文件",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tiff;*.tif;*.webp;*.psd;*.tga;*.svg|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
            _ = Pins.AddFromFilesAsync(dialog.FileNames);
    }

    internal static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoRunKey, writable: false);
        return key?.GetValue("Pinshot") != null;
    }

    internal static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutoRunKey, writable: true);
        if (enable)
            key.SetValue("Pinshot", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("Pinshot", throwOnMissingValue: false);
    }

    /// <summary>加载内嵌的应用图标（S/T）。</summary>
    private static BitmapSource LoadAppIcon()
    {
        var uri = new Uri("pack://application:,,,/Resources/icon_128.png", UriKind.Absolute);
        var source = BitmapFrame.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        source.Freeze();
        return source;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys.Dispose();
        // 先落盘再关窗口（关窗口会触发变更通知）
        Pins.SaveAllNow();
        _trayIcon?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 已释放或进程持有者退出 */ }
        }
        base.OnExit(e);
    }
}
