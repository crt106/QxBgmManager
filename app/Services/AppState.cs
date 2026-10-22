using System.IO;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>跨页面共享的曲库缓存：8 个 BeydMusic pck 的全部条目 + 曲名映射 + 补丁标记。</summary>
public static class AppState
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _revision;
    public static long Revision => Interlocked.Read(ref _revision);
    public static event Action? LibraryChanged;

    public static IReadOnlyList<PckEntry> Library { get; private set; } = [];
    public static Dictionary<uint, TrackInfo> TrackMap { get; private set; } = new();
    public static bool Loaded { get; private set; }
    public static string LoadedRoot { get; private set; } = "";

    /// <summary>加载（或复用缓存的）全部音乐条目；gameRoot 变化或 force 时重新解析。</summary>
    public static async Task<(bool Ok, string Message)> EnsureLibraryAsync(string gameRoot, bool force = false)
    {
        if (!SettingsService.IsValidGameRoot(gameRoot))
            return (false, "游戏根路径无效：未找到 BeyondUGC 目录，请先在设置页配置。");

        await Gate.WaitAsync();
        try
        {
            if (!force && Loaded && LoadedRoot == gameRoot)
                return (true, $"已加载 {Library.Count} 个条目（缓存）");
            return await Task.Run(() =>
            {
                long revision = Revision;
                try
                {
                    var map = TrackMapService.Load();
                    var list = new List<PckEntry>();
                    foreach (var pck in SettingsService.MusicPckPaths(gameRoot))
                        list.AddRange(PckService.ListEntries(pck));
                    TrackMap = map;
                    Library = list;
                    Loaded = revision == Revision;
                    LoadedRoot = gameRoot;
                    Interlocked.Increment(ref _revision);
                    return (true, $"已加载 {list.Count} 个条目");
                }
                catch (Exception ex)
                {
                    return (false, "曲库解析失败：" + ex.Message);
                }
            });
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>补丁/复原后调用：让下一次 EnsureLibraryAsync 重新读取 LUT 大小。</summary>
    public static void InvalidateLibrary()
    {
        Interlocked.Increment(ref _revision);
        Loaded = false;
        if (System.Windows.Application.Current is { } app)
            app.Dispatcher.BeginInvoke(() => LibraryChanged?.Invoke(), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>当前 LUT 里该 pck 的已补丁 fileID 集合（备份存在即 patched）。</summary>
    public static HashSet<uint> PatchedIds(string pckPath)
        => PckService.ReadBackup(pckPath)?.Entries.Select(e => e.FileId).ToHashSet()
           ?? new HashSet<uint>();

    /// <summary>fileID 的曲目信息；无映射返回 null（调用方按「未识别」处理）。</summary>
    public static TrackInfo? InfoOf(uint fileId)
        => TrackMap.TryGetValue(fileId, out var info) ? info : null;

    // ---- 索引仓库共享配置（RegistryService 同步后写入）----

    /// <summary>索引仓库 config/track-map.json 的缓存副本；优先级高于本地研究目录文件。</summary>
    public static Dictionary<uint, TrackInfo>? SharedTrackMap { get; private set; }

    /// <summary>索引仓库 config/app-config.json 的缓存副本（预设/书签等）。</summary>
    public static AppConfig? SharedConfig { get; private set; }

    public static void SetSharedConfigs(Dictionary<uint, TrackInfo>? trackMap, AppConfig? config)
    {
        SharedTrackMap = trackMap is { Count: > 0 } ? trackMap : null;
        SharedConfig = config;
    }

    /// <summary>扫描 8 个 pck 的全部活动补丁。</summary>
    public static List<PatchInfo> ScanPatches(string gameRoot)
    {
        var result = new List<PatchInfo>();
        foreach (var pck in SettingsService.MusicPckPaths(gameRoot))
        {
            var backup = PckService.ReadBackup(pck);
            if (backup == null || backup.Entries.Count == 0) continue;
            var time = File.GetLastWriteTime(PckService.BackupPath(pck));
            foreach (var e in backup.Entries)
            {
                result.Add(new PatchInfo
                {
                    PckPath = pck,
                    PckName = Path.GetFileName(pck),
                    FileId = e.FileId,
                    OriginalSize = e.Size,
                    BackupTime = time,
                });
            }
        }
        return result;
    }
}
