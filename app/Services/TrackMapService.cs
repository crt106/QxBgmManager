using System.IO;
using System.Text.Json;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// fileID → 曲目信息映射。优先级：registry 共享缓存（索引仓库 config/track-map.json，由
/// RegistryService 同步后写入 AppState.SharedTrackMap）→ 本地研究目录 track-map.json →
/// 2 条硬编码种子。文件缺失/损坏按下一级降级，绝不抛异常。
/// </summary>
public static class TrackMapService
{
    private static readonly (uint FileId, string Name, long Index)[] Seed =
    [
        (15673379, "无尽的挑战", 10140),
        (122217526, "难逃之阵", 10071),
    ];

    public static string MapFile => Path.Combine(SettingsService.ResearchDir, "track-map.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Dictionary<uint, TrackInfo> Load(string? mapFile = null)
    {
        // 优先级 1：索引仓库共享缓存
        if (AppState.SharedTrackMap is { Count: > 0 } shared)
            return new Dictionary<uint, TrackInfo>(shared);

        // 优先级 3 的种子先打底
        var map = new Dictionary<uint, TrackInfo>();
        foreach (var (fileId, name, index) in Seed)
            map[fileId] = new TrackInfo { FileId = fileId, Name = name, Index = index };

        // 优先级 2：本地研究目录文件覆盖
        try
        {
            mapFile ??= MapFile;
            if (!File.Exists(mapFile)) return map;
            foreach (var (fileId, info) in ParseTracksJson(File.ReadAllText(mapFile)))
                map[fileId] = info;
        }
        catch { /* 映射文件损坏时仅使用种子 */ }
        return map;
    }

    /// <summary>解析 tracks JSON（顶层数组或 {"tracks":[]} 包装）为 fileID → TrackInfo，不含种子。</summary>
    public static Dictionary<uint, TrackInfo> ParseTracksJson(string json)
    {
        var result = new Dictionary<uint, TrackInfo>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tracks = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tracks", out var t) ? t
            : default;
        if (tracks.ValueKind != JsonValueKind.Array) return result;
        foreach (var el in tracks.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("fileID", out var fidEl) || fidEl.ValueKind != JsonValueKind.Number) continue;
            var info = JsonSerializer.Deserialize<TrackInfo>(el.GetRawText(), JsonOpts);
            if (info == null || info.FileId == 0) continue;
            result[info.FileId] = info;
        }
        return result;
    }

    public static TrackInfo? InfoOf(IReadOnlyDictionary<uint, TrackInfo> map, uint fileId)
        => map.TryGetValue(fileId, out var info) ? info : null;

    /// <summary>主曲名（中文优先）；无映射时返回 fileID 字符串。</summary>
    public static string NameOf(IReadOnlyDictionary<uint, TrackInfo> map, uint fileId)
        => InfoOf(map, fileId) is { } info && !string.IsNullOrWhiteSpace(info.Name)
            ? info.PrimaryName
            : fileId.ToString();
}
