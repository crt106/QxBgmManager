using System.Text.Json.Serialization;

namespace QxBgmManager.Models;

/// <summary>
/// 索引仓库 config/app-config.json 共享应用配置。STJ 忽略未知字段；缺字段按默认值回退。
/// </summary>
public class AppConfig
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("minAppVersion")] public string MinAppVersion { get; set; } = "";
    [JsonPropertyName("encoder")] public EncoderConfig? Encoder { get; set; }
    [JsonPropertyName("presets")] public List<EncoderPreset> Presets { get; set; } = new();
    [JsonPropertyName("bookmarks")] public List<BrowserBookmark> Bookmarks { get; set; } = new();
    [JsonPropertyName("knownGameRoots")] public List<string> KnownGameRoots { get; set; } = new();
    [JsonPropertyName("audio")] public AudioConfig? Audio { get; set; }
}

public class EncoderConfig
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("sampleRate")] public int SampleRate { get; set; } = 48000;
    [JsonPropertyName("channels")] public int Channels { get; set; } = 2;
    [JsonPropertyName("beam")] public int Beam { get; set; } = 64;
}

/// <summary>编码预设：lowpassHz &gt; 0 时先低通再编码。</summary>
public class EncoderPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("lowpassHz")] public double LowpassHz { get; set; }
    [JsonPropertyName("loop")] public bool Loop { get; set; }
}

public class AudioConfig
{
    [JsonPropertyName("libraryDir")] public string LibraryDir { get; set; } = "";
    [JsonPropertyName("musicPckPattern")] public string MusicPckPattern { get; set; } = "";
    [JsonPropertyName("bytesPerSecond")] public double BytesPerSecond { get; set; } = 54000;
}
