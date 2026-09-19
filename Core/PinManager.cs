using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Pinshot.Views;

namespace Pinshot.Core;

/// <summary>重启恢复用的一条贴图状态（位置为缩放倍率 1 的基准坐标，物理像素）。</summary>
public sealed record PinPersistRecord(
    string File,
    int Left,
    int Top,
    double Scale,
    double Opacity,
    bool Compact,
    string BorderStyle = "None",
    string BorderColor = "#333333",
    bool Shadow = true);

/// <summary>回收站条目：关闭的贴图进入回收站，可还原（Setuna DustBox 行为）。</summary>
public sealed record RecycledPin(
    byte[] ImageData,
    bool IsGif,
    int Left,
    int Top,
    double Scale,
    double Opacity,
    bool Compact,
    string BorderStyle,
    string BorderColor,
    bool Shadow,
    DateTime ClosedAt);

/// <summary>
/// Setuna 式贴图窗口的集中管理：创建（截图/文件/网页/拖放）、显隐切换、激活全部、
/// 批量关闭、截图期间的 DWM 遮蔽避让，以及重启恢复（防抖落盘，带数量与体积上限）。
/// </summary>
public sealed class PinManager
{
    private const int MaxPins = 64;
    private const long MaxPersistBytes = 200 * 1024 * 1024;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    private readonly HashSet<PinWindow> _windows = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RecycledPin> _dustbox = [];
    private const int DustboxCapacity = 8;
    private DispatcherTimer? _saveTimer;
    private int _cascade;
    private bool _allHidden;

    internal bool IsCapturing { get; private set; }

    /// <summary>当前回收站内容（最新在前）。</summary>
    public IReadOnlyList<RecycledPin> Dustbox => _dustbox.AsReadOnly();

    /// <summary>当前存活贴图快照（供参考图名单显示）。</summary>
    public List<PinWindow> GetActiveSnapshot() =>
    [.. _windows];

    /// <summary>最近激活（点击/创建）的贴图——托盘单贴图操作的目标。</summary>
    public PinWindow? LastActivated { get; private set; }

    internal void NotifyActivated(PinWindow window) => LastActivated = window;

    private static string PinsDir => Path.Combine(ConfigStore.DirectoryPath, "pins");
    private static string IndexPath => Path.Combine(PinsDir, "index.json");

    #region 创建贴图

    /// <summary>创建贴图。bitmap 所有权移交贴图窗口，physicalBounds 为空时贴到光标附近。</summary>
    public PinWindow CreatePin(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle? physicalBounds = null, byte[]? gifBytes = null, string? sourcePath = null)
    {
        Application.Current.Dispatcher.VerifyAccess();
        var window = new PinWindow(this, bitmap, physicalBounds, gifBytes, sourcePath);
        _windows.Add(window);
        try
        {
            // 截图刚结束时立刻创建贴图，不能抢走焦点
            window.ShowActivated = !IsCapturing;
            window.Show();
        }
        catch
        {
            window.Close();
            throw;
        }
        NotifyStateChanged();
        return window;
    }

    /// <summary>从本地图片文件创建贴图（多选，级联摆放）。</summary>
    public async Task AddFromFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Take(MaxPins))
        {
            try
            {
                var loaded = await Task.Run(() => ImageLoader.LoadFromFile(path));
                if (_windows.Count >= MaxPins)
                    break;
                var bounds = NextCascadeBounds(loaded.Bitmap.Width, loaded.Bitmap.Height);
                CreatePin(loaded.Bitmap, bounds, loaded.GifBytes, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法加载 {Path.GetFileName(path)}：{ex.Message}",
                    "Pinshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>从网页图片 URL 下载并创建贴图（仅 http/https，拒绝内网地址）。</summary>
    public async Task AddFromWebAsync(string url)
    {
        try
        {
            var loaded = await ImageLoader.DownloadAsync(url);
            var bounds = NextCascadeBounds(loaded.Bitmap.Width, loaded.Bitmap.Height);
            CreatePin(loaded.Bitmap, bounds, loaded.GifBytes);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"下载图片失败：{ex.Message}", "Pinshot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>批量新建时的级联摆放：从光标（或主屏工作区中心）起每张偏移 26px。</summary>
    private System.Drawing.Rectangle NextCascadeBounds(int width, int height)
    {
        if (!Win32Helper.TryGetCursorPosition(out var cursor))
        {
            var area = SystemParameters.WorkArea;
            cursor = new System.Drawing.Point((int)(area.Left + area.Width / 2), (int)(area.Top + area.Height / 2));
        }
        var offset = (_cascade++ % 12) * 26;
        return new System.Drawing.Rectangle(cursor.X + offset, cursor.Y + offset, width, height);
    }

    #endregion

    #region 显隐 / 激活 / 关闭

    /// <summary>Setuna 行为：存在可见贴图时全部隐藏，否则全部恢复显示。</summary>
    public void ToggleVisibility()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(ToggleVisibility);
            return;
        }

        _allHidden = _windows.Count != 0 && !_allHidden;
        foreach (var window in _windows)
        {
            if (_allHidden)
                window.Hide();
            else
                window.Show();
        }
    }

    /// <summary>Setuna 行为：托盘点击/双击时把所有贴图重新提到最前。</summary>
    public void ActivateAll()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(ActivateAll);
            return;
        }

        foreach (var window in _windows)
            window.BringToFront();
    }

    /// <summary>对全部贴图批量执行操作（托盘“贴图功能”用）。</summary>
    public void ApplyToAll(Action<PinWindow> action)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => ApplyToAll(action));
            return;
        }
        foreach (var window in _windows)
            action(window);
    }

    public void CloseAll()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(CloseAll);
            return;
        }

        foreach (var window in _windows.ToArray())
            window.Close();
    }

    internal void Unregister(PinWindow window, bool toDustbox)
    {
        _windows.Remove(window);
        if (_windows.Count == 0)
            _allHidden = false;
        // 关闭 = 直接移除；销毁 = 进回收站（可还原）
        if (toDustbox)
            MoveToDustbox(window);
    }

    private void MoveToDustbox(PinWindow window)
    {
        try
        {
            var state = window.GetPersistRecord(string.Empty);
            if (!window.SaveImageBytes(out var imageData, out var isGif))
                return;
            _dustbox.Insert(0, new RecycledPin(
                imageData, isGif,
                state.Left, state.Top, state.Scale, state.Opacity, state.Compact,
                state.BorderStyle, state.BorderColor, state.Shadow,
                DateTime.Now));
            if (_dustbox.Count > Math.Clamp(App.Config.DustboxCapacity, 1, 50))
                _dustbox.RemoveRange(Math.Clamp(App.Config.DustboxCapacity, 1, 50), _dustbox.Count - Math.Clamp(App.Config.DustboxCapacity, 1, 50));
        }
        catch
        {
            // 回收站失败不影响关闭
        }
    }

    /// <summary>从回收站还原贴图（索引按 Dustbox 顺序）。</summary>
    public void RestoreFromDustbox(int index)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => RestoreFromDustbox(index));
            return;
        }

        if (index < 0 || index >= _dustbox.Count)
            return;
        var recycled = _dustbox[index];
        try
        {
            var loaded = ImageLoader.LoadFromBytes(recycled.ImageData);
            var gif = recycled.IsGif ? recycled.ImageData : null;
            var bounds = new System.Drawing.Rectangle(
                recycled.Left, recycled.Top, loaded.Bitmap.Width, loaded.Bitmap.Height);
            var window = new PinWindow(this, loaded.Bitmap, bounds, gif);
            window.ApplyPersisted(recycled.Scale, recycled.Opacity, recycled.Compact,
                recycled.BorderStyle, recycled.BorderColor, recycled.Shadow);
            _windows.Add(window);
            window.ShowActivated = false;
            window.Show();
            _dustbox.RemoveAt(index);
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"还原贴图失败：{ex.Message}", "Pinshot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>清空回收站（Setuna“清空回收站”）。</summary>
    public void ClearDustbox() => _dustbox.Clear();

    /// <summary>从回收站永久删除单条（不还原）。</summary>
    public void RemoveFromDustbox(int index)
    {
        if (index >= 0 && index < _dustbox.Count)
            _dustbox.RemoveAt(index);
    }

    #endregion

    #region 截图避让

    /// <summary>
    /// 开始一次截图：遮蔽所有贴图，返回租约；截图结束释放租约后恢复显示。
    /// 同一时刻只允许一次截图，重复触发返回 null。
    /// </summary>
    internal async ValueTask<IAsyncDisposable?> BeginCaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return null;

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsCapturing = true;
                foreach (var window in _windows)
                {
                    window.CloseTransientUiForCapture();
                    window.SetCloaked(true);
                }
                if (_windows.Count > 0)
                    Win32Helper.FlushDesktopComposition();
            });
            return new CaptureLease(this);
        }
        catch
        {
            await EndCaptureAsync();
            throw;
        }
    }

    private async Task EndCaptureAsync()
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsCapturing = false;
                foreach (var window in _windows)
                    window.SetCloaked(false);
                if (_windows.Count > 0)
                    Win32Helper.FlushDesktopComposition();
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class CaptureLease(PinManager owner) : IAsyncDisposable
    {
        private PinManager? _owner = owner;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } current)
                await current.EndCaptureAsync();
        }
    }

    #endregion

    #region 持久化（重启恢复）

    /// <summary>贴图集合发生变化（新建/移动/缩放/关闭等），防抖后落盘。</summary>
    internal void NotifyStateChanged()
    {
        if (!App.Config.PersistPins)
            return;
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(NotifyStateChanged);
            return;
        }

        _saveTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SaveDebounce,
        };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
        _saveTimer.Tick += OnSaveTimerTick;
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        // 收集（需 UI 线程）与写盘（IO）分离，避免大图 PNG 序列化卡住界面
        try
        {
            var payload = CollectPersistPayload();
            Task.Run(() =>
            {
                try { WritePersistPayload(payload); }
                catch { /* 写盘失败留给下次变更再试 */ }
            });
        }
        catch
        {
            // 收集失败不打断使用
        }
    }

    /// <summary>立即落盘（退出前调用）。</summary>
    public void SaveAllNow()
    {
        _saveTimer?.Stop();
        try
        {
            SaveAll();
        }
        catch
        {
            // 退出路径不再弹窗
        }
    }

    /// <summary>UI 线程收集：图片序列化到内存，记录元数据（不触碰磁盘 IO）。</summary>
    private List<(PinPersistRecord Record, byte[] Data, bool IsGif)> CollectPersistPayload()
    {
        Application.Current.Dispatcher.VerifyAccess();
        var payload = new List<(PinPersistRecord, byte[], bool)>();
        if (!App.Config.PersistPins)
            return payload;
        foreach (var window in _windows.Take(MaxPins))
        {
            if (!window.SaveImageBytes(out var data, out var isGif))
                continue;
            payload.Add((window.GetPersistRecord(string.Empty), data, isGif));
        }
        return payload;
    }

    /// <summary>后台线程写盘：图片文件 + 索引 + 清理旧文件与体积淘汰。</summary>
    private void WritePersistPayload(List<(PinPersistRecord Record, byte[] Data, bool IsGif)> payload)
    {
        if (!App.Config.PersistPins)
        {
            CleanPinsDir();
            return;
        }

        Directory.CreateDirectory(PinsDir);
        var records = new List<PinPersistRecord>();
        foreach (var (record, data, isGif) in payload)
        {
            var file = $"pin_{Guid.NewGuid().ToString("N")[..8]}{(isGif ? ".gif" : ".png")}";
            File.WriteAllBytes(Path.Combine(PinsDir, file), data);
            records.Add(record with { File = file });
        }

        // 清掉上一轮遗留、且不属于本次集合的文件；再按体积上限淘汰最旧
        var used = records.Select(r => Path.Combine(PinsDir, r.File)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(PinsDir))
        {
            if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!used.Contains(file))
            {
                try { File.Delete(file); } catch { /* 占用则留给下轮 */ }
            }
        }
        EnforceSizeCap(used);

        File.WriteAllText(IndexPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>同步落盘（退出前调用；UI 线程收集后直接写）。</summary>
    public void SaveAll()
    {
        var payload = CollectPersistPayload();
        WritePersistPayload(payload);
    }

    private void EnforceSizeCap(HashSet<string> keep)
    {
        var files = new List<(string Path, long Size, DateTime Time)>();
        foreach (var file in Directory.EnumerateFiles(PinsDir))
        {
            if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new FileInfo(file);
                files.Add((file, info.Length, info.LastWriteTimeUtc));
            }
            catch { /* 并发删除竞态，忽略 */ }
        }
        long total = 0;
        foreach (var (_, size, _) in files)
            total += size;
        if (total <= MaxPersistBytes)
            return;
        foreach (var (file, size, _) in files.OrderBy(f => f.Time))
        {
            if (total <= MaxPersistBytes)
                break;
            try { File.Delete(file); keep.Remove(file); } catch { }
            total -= size;
        }
    }

    /// <summary>应用启动时恢复上一轮未关闭的贴图。</summary>
    public void RestoreAll()
    {
        if (!App.Config.PersistPins || !File.Exists(IndexPath))
            return;

        List<PinPersistRecord> records;
        try
        {
            records = JsonSerializer.Deserialize<List<PinPersistRecord>>(File.ReadAllText(IndexPath)) ?? [];
        }
        catch
        {
            return; // index 损坏则放弃恢复
        }

        foreach (var record in records.Take(MaxPins))
        {
            try
            {
                var path = Path.Combine(PinsDir, record.File);
                if (!File.Exists(path))
                    continue;
                var loaded = ImageLoader.LoadFromFile(path);
                var bounds = new System.Drawing.Rectangle(
                    record.Left, record.Top, loaded.Bitmap.Width, loaded.Bitmap.Height);
                var window = new PinWindow(this, loaded.Bitmap, bounds, loaded.GifBytes);
                window.ApplyPersisted(record.Scale, record.Opacity, record.Compact,
                    record.BorderStyle, record.BorderColor, record.Shadow);
                _windows.Add(window);
                window.ShowActivated = false;
                window.Show();
            }
            catch
            {
                // 单张恢复失败跳过，不影响其余
            }
        }
    }

    private static void CleanPinsDir()
    {
        if (!Directory.Exists(PinsDir))
            return;
        try
        {
            Directory.Delete(PinsDir, true);
        }
        catch { /* 占用则留给下轮 */ }
    }

    #endregion
}
