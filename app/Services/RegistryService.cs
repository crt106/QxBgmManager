using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// qxbgm-registry/v1 索引仓库服务：本地路径直读 / git URL 浅克隆（LibGit2Sharp），
/// 解析 index/*.json 为市场条目，提供语义化版本比较与资产下载（https/本地/相对仓库路径）。
/// </summary>
public static class RegistryService
{
    public const string RegistryVersion = "qxbgm-registry/v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex IdPattern = new("^[a-zA-Z0-9-]+$", RegexOptions.Compiled);

    // ---- 索引源同步 ----

    public static bool IsGitUrl(string source)
        => Regex.IsMatch(source, @"^(https?|ssh|git)://") || source.EndsWith(".git", StringComparison.OrdinalIgnoreCase);

    /// <summary>git URL 源的本地浅克隆缓存目录：app\data\registry\&lt;host-hash&gt;\。</summary>
    public static string CloneCacheDir(string source)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source)))[..8].ToLowerInvariant();
        return Path.Combine(AppDataService.DataDir, "registry", hash);
    }

    /// <summary>
    /// 同步索引源并返回仓库目录。本地路径：直接读（git 仓库且有 origin 上游则 ff-only pull）；
    /// git URL：已存在则 fetch+硬重置到远端分支（等价 pull --ff-only），否则 depth 1 浅克隆。
    /// </summary>
    public static async Task<(string Dir, string Message)> SyncAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new SchemeException("索引源未配置，请在设置页填写本地路径或 git URL。");
        source = source.Trim();

        if (Directory.Exists(source))
        {
            if (Repository.IsValid(source))
            {
                using var repo = new Repository(source);
                if (repo.Network.Remotes["origin"] != null)
                {
                    try
                    {
                        var sig = new Signature("QxBgmManager", "qxbgm@local", DateTimeOffset.Now);
                        Commands.Pull(repo, sig, new PullOptions
                        {
                            MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly },
                        });
                        return (source, "已拉取最新索引");
                    }
                    catch (Exception ex)
                    {
                        return (source, "拉取失败，使用本地已有索引：" + ex.Message);
                    }
                }
                return (source, "本地 git 仓库（无上游），只读");
            }
            return (source, "本地索引目录");
        }

        if (IsGitUrl(source))
        {
            return await Task.Run(() =>
            {
                var dir = CloneCacheDir(source);
                if (Repository.IsValid(dir))
                {
                    using var repo = new Repository(dir);
                    Commands.Fetch(repo, "origin", Array.Empty<string>(), new FetchOptions(), null);
                    var tracked = repo.Head.TrackedBranch
                                  ?? repo.Branches[$"origin/{repo.Head.FriendlyName}"];
                    if (tracked != null)
                        repo.Reset(ResetMode.Hard, tracked.Tip);
                    return (dir, "已更新索引仓库缓存");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                // LibGit2Sharp 不支持 depth 浅克隆（git2 未暴露），索引仓库仅 JSON 小文件，全量克隆可接受
                Repository.Clone(source, dir, new CloneOptions());
                return (dir, "已克隆索引仓库");
            }, ct);
        }

        throw new SchemeException("索引源既不是本地路径也不是 git URL：" + source);
    }

    // ---- 条目解析 ----

    public static RegistryEntry ParseEntry(string json)
    {
        RegistryEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<RegistryEntry>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new SchemeException("条目 JSON 解析失败：" + ex.Message);
        }
        if (entry == null) throw new SchemeException("条目为空");
        if (entry.Registry != RegistryVersion)
            throw new SchemeException($"registry 版本不匹配：\"{entry.Registry}\"（仅支持 {RegistryVersion}）");
        if (string.IsNullOrWhiteSpace(entry.Id) || !IdPattern.IsMatch(entry.Id))
            throw new SchemeException("id 必填，仅限字母数字连字符：" + entry.Id);
        if (string.IsNullOrWhiteSpace(entry.Name))
            throw new SchemeException("name 必填");
        return entry;
    }

    /// <summary>解析 index/*.json；单条损坏跳过并记警告，不中断整体加载。</summary>
    public static (List<RegistryEntry> Entries, List<string> Warnings) LoadEntries(string repoDir)
    {
        var entries = new List<RegistryEntry>();
        var warnings = new List<string>();
        var dir = Path.Combine(repoDir, "index");
        if (!Directory.Exists(dir))
            return (entries, new List<string> { "索引仓库缺少 index/ 目录：" + repoDir });
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f))
        {
            try
            {
                entries.Add(ParseEntry(File.ReadAllText(file)));
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }
        return (entries, warnings);
    }

    // ---- 版本比较 ----

    /// <summary>语义化版本比较（点分数字，缺段补 0）：a&lt;b 返回负数，相等 0，a&gt;b 正数。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length ? pa[i] : 0;
            long y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    private static long[] ParseParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<long>();
        return version.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => long.TryParse(p, out var n) ? n : 0)
            .ToArray();
    }

    /// <summary>条目是否有新版本（未安装不计升级）。</summary>
    public static bool IsUpgradeAvailable(string? installedVersion, string? entryVersion)
        => !string.IsNullOrWhiteSpace(installedVersion)
           && !string.IsNullOrWhiteSpace(entryVersion)
           && CompareVersions(entryVersion, installedVersion) > 0;

    // ---- 资产下载 ----

    /// <summary>
    /// 落地资产文件：https URL → 下载；本地绝对路径 → 复制；相对路径 → 相对 baseDir（索引仓库目录）。
    /// sha256 存在则校验，不符抛 SchemeException。
    /// </summary>
    public static async Task FetchFileAsync(string urlOrPath, string? baseDir, string dest,
        string? sha256, Action<(long Done, long Total)>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        string source = urlOrPath;
        if (!IsHttp(source) && !Path.IsPathRooted(source))
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                throw new SchemeException("相对路径资产需要索引仓库目录：" + source);
            source = Path.GetFullPath(Path.Combine(baseDir, source));
        }

        if (IsHttp(source))
        {
            if (!source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new SchemeException("仅支持 https:// 下载：" + source);
            using var http = new HttpClient();
            using var response = await http.GetAsync(new Uri(source), HttpCompletionOption.ResponseHeadersRead, ct);
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
        else
        {
            if (!File.Exists(source))
                throw new SchemeException("资产文件不存在：" + source);
            File.Copy(source, dest, overwrite: true);
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            string actual = await SchemeService.Sha256FileAsync(dest, ct);
            if (!actual.Equals(sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new SchemeException($"sha256 校验失败：期望 {sha256[..Math.Min(16, sha256.Length)]}…，实际 {actual[..16]}…");
        }
    }

    private static bool IsHttp(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
