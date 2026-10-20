using System.Text.Json.Serialization;

namespace QxBgmManager.Models;

/// <summary>
/// track-map.json 单条曲目信息（扩展格式）。STJ 忽略未知字段，旧格式（仅 index/name/fileID）兼容。
/// </summary>
public class TrackInfo
{
    [JsonPropertyName("index")] public long Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("fileID")] public uint FileId { get; set; }
    [JsonPropertyName("pck")] public string Pck { get; set; } = "";
    [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
    [JsonPropertyName("match")] public string Match { get; set; } = "";
    [JsonPropertyName("score")] public double? Score { get; set; }
    /// <summary>101 探索 / 102 战斗 / 103 任务 / 104 其他；0 = 未识别。</summary>
    [JsonPropertyName("category")] public int Category { get; set; }
    [JsonPropertyName("categoryName")] public string CategoryName { get; set; } = "";
    [JsonPropertyName("album")] public string Album { get; set; } = "";

    /// <summary>曲名常为「中文 English」形式，拆出中文主名与英文副名。</summary>
    public (string Chinese, string English) SplitName()
    {
        if (string.IsNullOrWhiteSpace(Name)) return ("", "");
        int latin = -1;
        for (int i = 0; i < Name.Length; i++)
        {
            char c = Name[i];
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { latin = i; break; }
        }
        if (latin < 0) return (Name.Trim(), "");
        if (latin == 0) return ("", Name.Trim());
        return (Name[..latin].Trim(), Name[latin..].Trim());
    }

    /// <summary>展示主名：优先中文部分，否则全名。</summary>
    public string PrimaryName
    {
        get
        {
            var (chinese, _) = SplitName();
            return string.IsNullOrWhiteSpace(chinese) ? Name.Trim() : chinese;
        }
    }

    public string DisplayCategory => Category switch
    {
        101 => "探索音乐",
        102 => "战斗音乐",
        103 => "任务音乐",
        104 => "其他需求",
        _ => "未识别",
    };

    /// <summary>分类徽标颜色（深色主题）。</summary>
    public string CategoryColor => Category switch
    {
        101 => "#6EE7B7", // 探索 - 薄荷绿
        102 => "#F27A7A", // 战斗 - 红
        103 => "#F2C94C", // 任务 - 黄
        104 => "#7AA2F2", // 其他 - 蓝
        _ => "#8B90A0",   // 未识别 - 灰
    };
}
