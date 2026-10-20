using System.IO;
using System.Text.Json;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// fileID → 曲目信息映射。种子映射硬编码；若 build\bgm-inject-research\track-map.json 存在则加载
/// （扩展格式含 index/name/fileID/pck/durationSec/category/categoryName/album；
/// 兼容仅 index/name/fileID 的旧格式与 {"tracks":[...]} 包装；未知字段忽略）。
/// 文件不存在或损坏时仅使用种子，全部未映射条目按「未识别」处理。
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
        var map = new Dictionary<uint, TrackInfo>();
        foreach (var (fileId, name, index) in Seed)
            map[fileId] = new TrackInfo { FileId = fileId, Name = name, Index = index };
        try
        {
            mapFile ??= MapFile;
            if (!File.Exists(mapFile)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(mapFile));
            var root = doc.RootElement;
            var tracks = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tracks", out var t) ? t
                : default;
            if (tracks.ValueKind != JsonValueKind.Array) return map;
            foreach (var el in tracks.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("fileID", out var fidEl) || fidEl.ValueKind != JsonValueKind.Number) continue;
                // 逐条容错：单条损坏（如 score:null 对非可空字段）不影响其他条目
                TrackInfo? info = null;
                try { info = JsonSerializer.Deserialize<TrackInfo>(el.GetRawText(), JsonOpts); }
                catch { continue; }
                if (info == null || info.FileId == 0) continue;
                // 种子以外按文件数据覆盖
                map[info.FileId] = info;
            }
        }
        catch { /* 映射文件损坏时仅使用种子 */ }
        return map;
    }

    public static TrackInfo? InfoOf(IReadOnlyDictionary<uint, TrackInfo> map, uint fileId)
        => map.TryGetValue(fileId, out var info) ? info : null;

    /// <summary>主曲名（中文优先）；无映射时返回 fileID 字符串。</summary>
    public static string NameOf(IReadOnlyDictionary<uint, TrackInfo> map, uint fileId)
        => InfoOf(map, fileId) is { } info && !string.IsNullOrWhiteSpace(info.Name)
            ? info.PrimaryName
            : fileId.ToString();
}
