using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// qxbgm/v1 方案（QXBGM_SCHEME.md）解析、校验、音频落地与导出。
/// 分发形态：.qxbgm.json 单文件（音频走 https URL）与 .qxbgmzip（manifest.json + audio/）。
/// </summary>
public static class SchemeService
{
    public const string SchemeVersion = "qxbgm/v1";
    public const string KindRaw = "raw";
    public const string KindWem = "wem";

    /// <summary>登记与缓存目录：音频缓存与 wem 产物放 schemes\&lt;id&gt;\ 下。</summary>
    public static string SchemesRoot => Path.Combine(SettingsService.ResearchDir, "schemes");
    public static string SchemeDir(string schemeId) => Path.Combine(SchemesRoot, schemeId);
    public static string SchemeAudioDir(string schemeId) => Path.Combine(SchemeDir(schemeId), "audio");
    public static string SchemeWemDir(string schemeId) => Path.Combine(SchemeDir(schemeId), "wem");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly Regex IdPattern = new("^[a-zA-Z0-9-]+$", RegexOptions.Compiled);

    // ---- 解析（未知顶层字段默认被忽略 = 前向兼容；scheme 版本不符拒绝）----

    public static SchemeManifest ParseManifest(string json)
    {
        SchemeManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SchemeManifest>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new SchemeException("清单 JSON 解析失败：" + ex.Message);
        }
        if (manifest == null) throw new SchemeException("清单内容为空");
        if (manifest.Scheme != SchemeVersion)
            throw new SchemeException($"scheme 版本不匹配：\"{manifest.Scheme}\"（仅支持 {SchemeVersion}）");
        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern.IsMatch(manifest.Id))
            throw new SchemeException("id 必填，仅限字母数字连字符：" + manifest.Id);
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new SchemeException("name 必填");
        if (manifest.Tracks.Count == 0)
            throw new SchemeException("tracks 至少 1 条");

        foreach (var t in manifest.Tracks)
        {
            if (t.Target == null || t.Target.FileId == 0)
                throw new SchemeException("track.target.fileID 必填");
            if (t.Audio == null || string.IsNullOrWhiteSpace(t.Audio.Uri))
                throw new SchemeException($"track(fileID {t.Target.FileId}) 缺少 audio.uri");
            if (t.Audio.Kind != KindRaw && t.Audio.Kind != KindWem)
                throw new SchemeException($"track(fileID {t.Target.FileId}) audio.kind 必须是 raw 或 wem：{t.Audio.Kind}");
            if (t.Encode != null && t.Encode.Preset != "normal" && t.Encode.Preset != "chiptune-6k")
            {
                manifest.Warnings.Add($"fileID {t.Target.FileId}：未知编码预设 \"{t.Encode.Preset}\"，按 normal 处理");
                t.Encode.Preset = "normal";
            }
            if (IsHttpUrl(t.Audio.Uri, out var scheme) && scheme == "http")
                throw new SchemeException($"track(fileID {t.Target.FileId}) 音频地址不安全，仅支持 https：{t.Audio.Uri}");
        }
        return manifest;
    }

    /// <summary>读取 .qxbgm.json 单文件或 .qxbgmzip 包（读包内 manifest.json）。</summary>
    public static SchemeManifest LoadManifestFile(string path)
    {
        if (path.EndsWith(".qxbgmzip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("manifest.json")
                        ?? throw new SchemeException("包内缺少 manifest.json");
            using var reader = new StreamReader(entry.Open());
            return ParseManifest(reader.ReadToEnd());
        }
        return ParseManifest(File.ReadAllText(path));
    }

    // ---- 目标定位与校验 ----

    /// <summary>按 target 定位曲库条目；pck 指定时只查该 pck，否则全库索引。</summary>
    public static PckEntry? FindTarget(SchemeTrack track, IReadOnlyList<PckEntry> library)
    {
        var query = library.Where(e => e.FileId == track.Target.FileId);
        if (!string.IsNullOrWhiteSpace(track.Target.Pck))
            query = query.Where(e => string.Equals(e.PckName, track.Target.Pck, StringComparison.OrdinalIgnoreCase));
        return query.FirstOrDefault();
    }

    // ---- 音频落地 ----

    /// <summary>
    /// 音频落地并校验 sha256：URL → 下载到 schemes\&lt;id&gt;\audio\；包内相对路径 → 从 zip 解出；
    /// 本地绝对路径 → 直接使用；**相对路径 + baseUri** → 相对 manifest 所在位置（https URL 目录或
    /// 本地目录）解析。返回落地后的文件路径。
    /// </summary>
    public static async Task<string> ResolveAudioAsync(SchemeManifest manifest, SchemeTrack track,
        string? packageZip, Action<(long Done, long Total)>? onProgress, CancellationToken ct,
        string? baseUri = null)
    {
        string uri = track.Audio.Uri;
        string path;
        if (IsHttpUrl(uri, out var scheme) && scheme == "https")
        {
            var destDir = SchemeAudioDir(manifest.Id);
            Directory.CreateDirectory(destDir);
            string fileName = Path.GetFileName(new Uri(uri).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = $"{track.Target.FileId}";
            string dest = Path.Combine(destDir, fileName);
            await DownloadAsync(new Uri(uri), dest, onProgress, ct);
            path = dest;
        }
        else if (Path.IsPathRooted(uri))
        {
            // 本地绝对路径（注意：须先于 Uri.TryCreate 判断，Windows 路径会被解析成 file:// URI）
            if (!File.Exists(uri))
                throw new SchemeException("本地音频文件不存在：" + uri);
            path = uri;
        }
        else if (!string.IsNullOrWhiteSpace(baseUri))
        {
            // 相对 manifest 所在位置解析（https URL 或本地 manifest 文件路径/目录）
            string resolved = ResolveAudioLocation(uri, baseUri);
            if (IsHttpUrl(resolved, out var rs) && rs == "https")
            {
                var destDir = SchemeAudioDir(manifest.Id);
                Directory.CreateDirectory(destDir);
                string fileName = Path.GetFileName(new Uri(resolved).LocalPath);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = $"{track.Target.FileId}";
                string dest = Path.Combine(destDir, fileName);
                await DownloadAsync(new Uri(resolved), dest, onProgress, ct);
                path = dest;
            }
            else
            {
                if (!File.Exists(resolved))
                    throw new SchemeException("本地音频文件不存在：" + resolved);
                path = resolved;
            }
        }
        else if (!string.IsNullOrWhiteSpace(packageZip) && File.Exists(packageZip))
        {
            var destDir = SchemeAudioDir(manifest.Id);
            Directory.CreateDirectory(destDir);
            // 防 zip-slip：只取文件名部分
            string safeName = Path.GetFileName(uri.Replace('/', Path.DirectorySeparatorChar));
            string dest = Path.Combine(destDir, safeName);
            using (var zip = ZipFile.OpenRead(packageZip))
            {
                var entry = zip.GetEntry(uri.Replace('\\', '/'))
                            ?? throw new SchemeException($"包内缺少音频条目：{uri}");
                using var input = entry.Open();
                await using var output = File.Create(dest);
                await input.CopyToAsync(output, ct);
            }
            path = dest;
        }
        else
        {
            throw new SchemeException("包内相对路径音频需要 .qxbgmzip 包或 manifest 基地址：" + uri);
        }

        if (!string.IsNullOrWhiteSpace(track.Audio.Sha256))
        {
            string actual = await Sha256FileAsync(path, ct);
            if (!actual.Equals(track.Audio.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new SchemeException(
                    $"sha256 校验失败：期望 {track.Audio.Sha256[..Math.Min(16, track.Audio.Sha256.Length)]}…，实际 {actual[..16]}…");
        }
        return path;
    }

    /// <summary>
    /// 把相对音频 uri 解析为绝对位置（不下载、不查存在性）。
    /// baseUri 为 manifest 的 https URL 或本地文件路径/目录；返回 https URL 字符串或本地绝对路径。
    /// </summary>
    public static string ResolveAudioLocation(string uri, string baseUri)
    {
        if (IsHttpUrl(uri, out _))
            return uri;
        if (IsHttpUrl(baseUri, out var bs) && bs == "https")
            return new Uri(new Uri(baseUri), uri).ToString();
        if (Path.IsPathRooted(baseUri))
        {
            // 目录基地址直接用；否则按 manifest 文件路径取其所在目录
            string dir = Directory.Exists(baseUri)
                ? baseUri
                : Path.GetDirectoryName(baseUri.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? baseUri;
            return Path.GetFullPath(Path.Combine(dir, uri.Replace('/', Path.DirectorySeparatorChar)));
        }
        throw new SchemeException("manifest 基地址仅支持 https URL 或本地绝对路径：" + baseUri);
    }

    private static async Task DownloadAsync(Uri uri, string dest, Action<(long, long)>? onProgress, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            onProgress?.Invoke((done, total));
        }
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsHttpUrl(string uri, out string scheme)
    {
        scheme = "";
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            scheme = u.Scheme;
            return true;
        }
        return false;
    }

    /// <summary>kind=raw 编码产物的落盘路径。</summary>
    public static string RawWemPath(string schemeId, SchemeTrack track)
    {
        var dir = SchemeWemDir(schemeId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{track.Target.FileId}.wem");
    }

    /// <summary>
    /// 安装单条轨道：音频落地（zip/URL/本地/基地址）→ raw 编码 → 容量复核 → patch。
    /// 条目已补丁时返回 null（跳过）。返回登记用 InstalledTrack。
    /// </summary>
    public static async Task<InstalledTrack?> InstallTrackAsync(SchemeManifest manifest, SchemeTrack track,
        PckEntry entry, string? packageZip, string? baseUri,
        EncodeService encoder, Action<string> log, CancellationToken ct, Action<EncodeProgress>? onProgress = null)
    {
        string audioPath = await ResolveAudioAsync(manifest, track, packageZip, null, ct, baseUri);
        string wemPath;
        if (track.Audio.Kind == KindRaw)
        {
            wemPath = RawWemPath(manifest.Id, track);
            var preset = track.Encode?.Preset ?? "normal";
            int rc = await encoder.EncodeAsync(audioPath, wemPath, PresetService.HzForPreset(preset),
                log, ct, loop: track.Encode?.Loop ?? false, onProgress: onProgress);
            if (rc != 0 || !File.Exists(wemPath))
                throw new SchemeException("编码失败（退出码 " + rc + "）");
        }
        else
        {
            wemPath = audioPath;
        }

        // 容量复核（重读条目，避免曲库缓存过期）
        var fresh = PckService.GetEntry(entry.PckPath, track.Target.FileId);
        long wemSize = new FileInfo(wemPath).Length;
        if (wemSize > fresh.Size)
            throw new SchemeException($"产物 {wemSize:N0} B 超出条目容量 {fresh.Size:N0} B");

        if (PckService.IsEntryPatched(fresh.PckPath, fresh.FileId))
            return null; // 已补丁，跳过
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => PckService.Patch(fresh.PckPath, fresh.FileId, wemPath), ct);
        return new InstalledTrack { PckPath = fresh.PckPath, PckName = fresh.PckName, FileId = fresh.FileId };
    }

    // ---- 导出 .qxbgmzip ----

    public class ExportItem
    {
        public string PckPath { get; set; } = "";
        public uint FileId { get; set; }
        /// <summary>targetRef 曲名（取自曲名映射服务）。</summary>
        public string RefName { get; set; } = "";
    }

    /// <summary>把一组 patched 条目打包为 .qxbgmzip：当前 wem 字节作为 kind=wem 音频，接收方免编码。</summary>
    public static string ExportSchemeZip(string outPath, string id, string name, string author,
        string description, IEnumerable<ExportItem> items)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
            throw new SchemeException("方案 ID 必填，仅限字母数字连字符");
        if (string.IsNullOrWhiteSpace(name))
            throw new SchemeException("方案名称必填");

        var itemList = items.ToList();
        if (itemList.Count == 0)
            throw new SchemeException("没有可导出的补丁条目");

        var manifest = new SchemeManifest
        {
            Scheme = SchemeVersion,
            Id = id,
            Name = name,
            Author = author,
            Description = description,
            CreatedAt = DateTimeOffset.Now.ToString("o"),
            Tracks = itemList.Select(i => new SchemeTrack
            {
                Target = new SchemeTarget { FileId = i.FileId, Pck = Path.GetFileName(i.PckPath) },
                TargetRef = new SchemeTargetRef { Name = i.RefName },
                Audio = new SchemeAudio { Uri = $"audio/{i.FileId}.wem", Kind = KindWem },
            }).ToList(),
        };

        if (File.Exists(outPath)) File.Delete(outPath);
        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
            writer.Write(JsonSerializer.Serialize(manifest, JsonOpts));
        foreach (var item in itemList)
        {
            var bytes = File.ReadAllBytes(ExtractCurrentWem(item));
            var entry = zip.CreateEntry($"audio/{item.FileId}.wem");
            using var output = entry.Open();
            output.Write(bytes, 0, bytes.Length);
        }
        return outPath;
    }

    private static string ExtractCurrentWem(ExportItem item)
    {
        // 从 pck 抽取 patched 条目的当前字节（临时文件，zip 写完后由 CleanupTempFiles 删除）
        string tmp = Path.Combine(Path.GetTempPath(), $"qxbgm-export-{item.FileId}-{Guid.NewGuid():N}.wem");
        PckService.ExtractEntry(item.PckPath, item.FileId, tmp);
        PendingTempDeletes.Add(tmp);
        return tmp;
    }

    private static readonly List<string> PendingTempDeletes = new();

    /// <summary>清理导出时产生的临时 wem 文件（页面在导出完成后调用）。</summary>
    public static void CleanupTempFiles()
    {
        foreach (var tmp in PendingTempDeletes.ToList())
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        PendingTempDeletes.Clear();
    }
}
