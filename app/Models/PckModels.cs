using System.Text.Json.Serialization;
using QxBgmManager.Services;

namespace QxBgmManager.Models;

/// <summary>pck 流式媒体 LUT 中的一个音乐条目（对应 bgm_swap.py list 的一行）。</summary>
public class PckEntry
{
    public string PckPath { get; set; } = "";
    public string PckName { get; set; } = "";
    public uint FileId { get; set; }
    public uint Lang { get; set; }
    /// <summary>条目容量（字节）。补丁状态下载 LUT 中存的是新 wem 大小，不再是原始容量。</summary>
    public uint Size { get; set; }
    public ulong Offset { get; set; }
    /// <summary>该条目在 pck 内 LUT 中的写入位置（字节偏移）。</summary>
    public long LutPos { get; set; }

    /// <summary>估算时长（PTADPCM 48kHz 立体声约 54000 B/s）。</summary>
    public double DurationSeconds => Size / PckService.BytesPerSecond;
}

/// <summary>
/// &lt;pck&gt;.bgmbackup 的 JSON 模型。字段名与 bgm_swap.py 逐一对齐，
/// 保证 Python CLI 与本应用互相识别对方写入的备份。
/// </summary>
public class BgmBackup
{
    [JsonPropertyName("pck")] public string Pck { get; set; } = "";
    [JsonPropertyName("pck_length")] public long? PckLength { get; set; }
    [JsonPropertyName("original_pck_sha256")] public string? OriginalPckSha256 { get; set; }
    [JsonPropertyName("entries")] public List<BackupEntry> Entries { get; set; } = new();
}

public class BackupEntry
{
    [JsonPropertyName("fileID")] public uint FileId { get; set; }
    [JsonPropertyName("offset")] public ulong Offset { get; set; }
    [JsonPropertyName("size")] public uint Size { get; set; }
    [JsonPropertyName("lut_pos")] public long LutPos { get; set; }
    [JsonPropertyName("orig_wem_b64")] public string? OrigWemB64 { get; set; }
    [JsonPropertyName("orig_wem_file")] public string? OrigWemFile { get; set; }
    [JsonPropertyName("orig_lut_hex")] public string? OrigLutHex { get; set; }
    [JsonPropertyName("orig_wem_sha256")] public string? OrigWemSha256 { get; set; }
    [JsonPropertyName("replacement_file")] public string? ReplacementFile { get; set; }
    [JsonPropertyName("replacement_sha256")] public string? ReplacementSha256 { get; set; }
}

/// <summary>补丁管理页的一行（一个 pck 内的一条活动补丁）。</summary>
public class PatchInfo
{
    public string PckPath { get; set; } = "";
    public string PckName { get; set; } = "";
    public uint FileId { get; set; }
    /// <summary>备份记录的原始条目大小（字节）。</summary>
    public uint OriginalSize { get; set; }
    /// <summary>.bgmbackup 文件的修改时间，即最近一次打入补丁的时间。</summary>
    public DateTime BackupTime { get; set; }
}
