using System.Text.Json.Serialization;

namespace QxBgmManager.Models;

// ---- qxbgm/v1 方案清单（QXBGM_SCHEME.md）----

public class SchemeManifest
{
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("wonderland")] public SchemeWonderland? Wonderland { get; set; }
    [JsonPropertyName("tracks")] public List<SchemeTrack> Tracks { get; set; } = new();

    /// <summary>解析期产生的警告（如未知编码预设按 normal 处理）。不入 JSON。</summary>
    [JsonIgnore] public List<string> Warnings { get; } = new();
}

public class SchemeWonderland
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class SchemeTrack
{
    [JsonPropertyName("target")] public SchemeTarget Target { get; set; } = new();
    [JsonPropertyName("targetRef")] public SchemeTargetRef? TargetRef { get; set; }
    [JsonPropertyName("audio")] public SchemeAudio Audio { get; set; } = new();
    [JsonPropertyName("encode")] public SchemeEncode? Encode { get; set; }
}

public class SchemeTarget
{
    [JsonPropertyName("fileID")] public uint FileId { get; set; }
    /// <summary>可选；省略时按全库索引定位。</summary>
    [JsonPropertyName("pck")] public string? Pck { get; set; }
}

public class SchemeTargetRef
{
    [JsonPropertyName("index")] public long? Index { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class SchemeAudio
{
    /// <summary>包内相对路径 / https URL / 本地绝对路径。</summary>
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    /// <summary>raw=原始音频需编码 | wem=已编码 PTADPCM 直接注入。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("durationHintSec")] public double? DurationHintSec { get; set; }
}

public class SchemeEncode
{
    /// <summary>normal | chiptune-6k；未知值按 normal 处理并警告。</summary>
    [JsonPropertyName("preset")] public string Preset { get; set; } = "normal";
    [JsonPropertyName("loop")] public bool Loop { get; set; }
}

// ---- 已安装方案登记（应用数据目录 installed-schemes.json）----

public class InstalledScheme
{
    [JsonPropertyName("status")] public string Status { get; set; } = "已安装";
    [JsonPropertyName("lastError")] public string LastError { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("installedAt")] public DateTime InstalledAt { get; set; }
    [JsonPropertyName("tracks")] public List<InstalledTrack> Tracks { get; set; } = new();
    /// <summary>来源市场条目的 id；空 = 手动安装的散包（不参与升级检查）。</summary>
    [JsonPropertyName("registryId")] public string? RegistryId { get; set; }
    /// <summary>安装时的市场条目版本号。</summary>
    [JsonPropertyName("registryVersion")] public string? RegistryVersion { get; set; }
}

public class InstalledTrack
{
    [JsonPropertyName("wemSha256")] public string? WemSha256 { get; set; }
    [JsonPropertyName("originalSha256")] public string? OriginalSha256 { get; set; }
    [JsonPropertyName("pckPath")] public string PckPath { get; set; } = "";
    [JsonPropertyName("pckName")] public string PckName { get; set; } = "";
    [JsonPropertyName("fileID")] public uint FileId { get; set; }
}

// ---- 奇域档案（应用数据目录 wonder-archive.json）----

public class WonderEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("schemeId")] public string SchemeId { get; set; } = "";
}

// ---- 浏览器书签（应用数据目录 bookmarks.json）----

public class BrowserBookmark
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

/// <summary>方案格式错误（解析/校验/下载/校验和）。</summary>
public class SchemeException : Exception
{
    public SchemeException(string message) : base(message) { }
}
