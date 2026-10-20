using System.Text.Json.Serialization;

namespace QxBgmManager.Models;

/// <summary>qxbgm-registry/v1 索引注册条目（index/&lt;id&gt;.json）。STJ 忽略未知字段。</summary>
public class RegistryEntry
{
    [JsonPropertyName("registry")] public string Registry { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    /// <summary>自由分类，市场页筛选用。</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    /// <summary>语义化版本，App 据此提示升级。</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("wonderland")] public RegistryWonderland? Wonderland { get; set; }
    /// <summary>manifest（.qxbgm.json）下载信息；与 package 二选一或并存，优先 package。</summary>
    [JsonPropertyName("manifest")] public RegistryAsset? Manifest { get; set; }
    /// <summary>整包 .qxbgmzip 下载信息。</summary>
    [JsonPropertyName("package")] public RegistryAsset? Package { get; set; }
    [JsonPropertyName("tracks")] public int Tracks { get; set; }
    [JsonPropertyName("minAppVersion")] public string MinAppVersion { get; set; } = "";
}

public class RegistryWonderland
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class RegistryAsset
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
}
