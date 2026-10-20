using System.IO;
using System.Text.Json;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>应用数据目录（与工作区 app\ 下的 appsettings.json 同级策略：跟工程走）+ 通用 JSON 读写。</summary>
public static class AppDataService
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(SettingsService.FindWorkspaceRoot(), "app", "data");
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
        try
        {
            File.WriteAllText(Path.Combine(DataDir, fileName),
                JsonSerializer.Serialize(value, JsonOpts));
        }
        catch { /* 只读目录等场景忽略 */ }
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
        var list = AppDataService.LoadJson<List<BrowserBookmark>>(FileName);
        if (list is not { Count: > 0 })
            return Defaults.Select(b => new BrowserBookmark { Title = b.Title, Url = b.Url }).ToList();
        // 合并新增的内置书签（按 Url 去重），老用户也能看到后续版本新增的默认项
        var urls = new HashSet<string>(list.Select(b => b.Url), StringComparer.OrdinalIgnoreCase);
        foreach (var b in Defaults)
            if (urls.Add(b.Url))
                list.Insert(0, new BrowserBookmark { Title = b.Title, Url = b.Url });
        return list;
    }

    public static void Save(IEnumerable<BrowserBookmark> bookmarks)
        => AppDataService.SaveJson(FileName, bookmarks.ToList());
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

    public static List<InstalledScheme> Load() => AppDataService.LoadJson<List<InstalledScheme>>(FileName) ?? new();

    public static void Save(IEnumerable<InstalledScheme> schemes)
        => AppDataService.SaveJson(FileName, schemes.ToList());

    public static void Upsert(InstalledScheme scheme)
    {
        var list = Load();
        list.RemoveAll(s => s.Id == scheme.Id);
        list.Add(scheme);
        Save(list);
    }

    public static void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(s => s.Id == id);
        Save(list);
    }
}
