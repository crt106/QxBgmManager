using System.IO;
using System.Text.Json;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>应用数据目录（与工作区 app\ 下的 appsettings.json 同级策略：跟工程走）+ 通用 JSON 读写。</summary>
public static class AppDataService
{
    internal static string? DirectoryOverride { get; set; }
    public static string DataDir
    {
        get
        {
            var dir = DirectoryOverride ?? Path.Combine(SettingsService.FindWorkspaceRoot(), "app", "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static T? LoadJson<T>(string fileName) where T : new()
    {
        try
        {
            var path = Path.Combine(DataDir, fileName);
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch { return default; }
    }

    public static void SaveJson(string fileName, object value)
    {
        string path = Path.Combine(DataDir, fileName), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(f, value, JsonOpts);
                f.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

/// <summary>书签（默认内置官方指南/米游社/HoYoLAB，可增删）。</summary>
public static class BookmarkService
{
    private const string FileName = "bookmarks.json";

    private static readonly BrowserBookmark[] Defaults =
    [
        new() { Title = "渊星助手·奇域数据", Url = "https://ugc.seadp.cn/query" },
        new() { Title = "渊星助手·用户查询", Url = "https://ugc.seadp.cn/ugc-db" },
        new() { Title = "官方指南", Url = "https://act.mihoyo.com/ys/ugc/tutorial" },
        new() { Title = "米游社", Url = "https://www.miyoushe.com/ys/" },
        new() { Title = "HoYoLAB", Url = "https://www.hoyolab.com/" },
    ];

    public static List<BrowserBookmark> Load()
    {
        // 索引仓库 app-config.json 的共享书签优先；用户在本地新增的书签按 Url 合并保留
        var shared = AppState.SharedConfig?.Bookmarks;
        if (shared is { Count: > 0 })
        {
            var result = shared.Select(b => new BrowserBookmark { Title = b.Title, Url = b.Url }).ToList();
            var urls = new HashSet<string>(result.Select(b => b.Url), StringComparer.OrdinalIgnoreCase);
            var custom = AppDataService.LoadJson<List<BrowserBookmark>>(FileName);
            if (custom != null)
                foreach (var b in custom)
                    if (urls.Add(b.Url))
                        result.Add(new BrowserBookmark { Title = b.Title, Url = b.Url });
            return result;
        }

        var list = AppDataService.LoadJson<List<BrowserBookmark>>(FileName);
        if (list is not { Count: > 0 })
            return Defaults.Select(b => new BrowserBookmark { Title = b.Title, Url = b.Url }).ToList();
        // 合并新增的内置书签（按 Url 去重），老用户也能看到后续版本新增的默认项
        var urls2 = new HashSet<string>(list.Select(b => b.Url), StringComparer.OrdinalIgnoreCase);
        foreach (var b in Defaults)
            if (urls2.Add(b.Url))
                list.Insert(0, new BrowserBookmark { Title = b.Title, Url = b.Url });
        return list;
    }

    public static void Save(IEnumerable<BrowserBookmark> bookmarks)
        => AppDataService.SaveJson(FileName, bookmarks.ToList());
}

/// <summary>编码预设：优先索引仓库 app-config.json 的 presets；取不到回退硬编码两项。</summary>
public static class PresetService
{
    private static readonly List<EncoderPreset> Fallback =
    [
        new() { Id = "normal", Name = "普通音乐", Description = "直接 beam64 编码，适合人声/器乐", LowpassHz = 0, Loop = false },
        new() { Id = "chiptune-6k", Name = "8bit 风格", Description = "先 6kHz 低通再编码", LowpassHz = 6000, Loop = false },
    ];

    public static IReadOnlyList<EncoderPreset> GetPresets()
        => AppState.SharedConfig?.Presets is { Count: > 0 } shared ? shared : Fallback;

    /// <summary>预设 id → 低通频率（Hz，0 = 不低通）；未知 id 按回退表匹配。</summary>
    public static double HzForPreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return 0;
        var preset = GetPresets().FirstOrDefault(p => p.Id == presetId)
                     ?? Fallback.FirstOrDefault(p => p.Id == presetId);
        return preset?.LowpassHz ?? 0;
    }
}

/// <summary>奇域档案本地登记表（后续按奇域ID 关联 BGM 方案的储备）。</summary>
public static class WonderArchiveService
{
    private const string FileName = "wonder-archive.json";

    public static List<WonderEntry> Load() => AppDataService.LoadJson<List<WonderEntry>>(FileName) ?? new();

    public static void Save(IEnumerable<WonderEntry> entries)
        => AppDataService.SaveJson(FileName, entries.ToList());
}

/// <summary>已安装方案登记：卸载 = 对登记曲目逐条 restore。</summary>
public static class InstallRegistryService
{
    private const string FileName = "installed-schemes.json";
    private static readonly object Gate = new();

    public static List<InstalledScheme> Load()
    {
        lock (Gate)
        {
            string path = Path.Combine(AppDataService.DataDir, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<InstalledScheme>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("方案登记损坏，请保留登记文件后重试") : new();
        }
    }

    public static void Save(IEnumerable<InstalledScheme> schemes)
        => AppDataService.SaveJson(FileName, schemes.ToList());

    public static void Upsert(InstalledScheme scheme)
    {
        lock (Gate)
        {
        var list = Load();
        list.RemoveAll(s => s.Id == scheme.Id);
        list.Add(scheme);
        Save(list);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
        var list = Load();
        list.RemoveAll(s => s.Id == id);
        Save(list);
        }
    }
}
