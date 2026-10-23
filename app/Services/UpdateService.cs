using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace QxBgmManager.Services;

public class UpdateInfo
{
    public bool HasUpdate { get; init; }
    public string Version { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
    public string Body { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// 应用自动更新（GitHub Releases 固定源）：静默检查 → 带进度下载 → cmd 脚本自替换并重启。
/// 单文件发布版执行自替换；框架依赖/调试运行（dotnet xxx.dll）只提示 git pull。
/// </summary>
public static class UpdateService
{
    public const string LatestReleaseApi = "https://api.github.com/repos/crt106/QxBgmManager/releases/latest";
    public const string AssetName = "QxBgmManager.exe";
    public const string DownloadedExeName = "QxBgmManager_new.exe";
    private const string ScriptFileName = "qxbgm_update.cmd";

    /// <summary>当前应用版本（InformationalVersion，剥离 git hash 后缀；兜底 AssemblyVersion）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            return ParseVersion(info)
                   ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                   ?? "0.1.2";
        }
    }

    /// <summary>框架依赖/调试运行（非单文件发布）时不能自替换 exe。</summary>
    public static bool IsFrameworkDependentRun
#pragma warning disable IL3000 // 刻意利用单文件发布下 Location 为空来判定发布形态
        => !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);
#pragma warning restore IL3000

    /// <summary>
    /// 查询 GitHub latest release。网络失败/解析失败/速率限制一律按"无更新"静默返回（Error 记原因）。
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync(string? currentVersion = null, CancellationToken ct = default)
    {
        currentVersion ??= CurrentVersion;
        try
        {
            using var http = new HttpClient();
            // GitHub API 要求 User-Agent，匿名 60 次/小时
            http.DefaultRequestHeaders.UserAgent.ParseAdd("QxBgmManager");
            var json = await http.GetStringAsync(LatestReleaseApi, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : "";
            var version = ParseVersion(tag);
            if (version == null)
                return new UpdateInfo { Error = "无法解析 tag_name：" + tag };

            string url = "";
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n) && n.GetString() == AssetName &&
                        a.TryGetProperty("browser_download_url", out var u))
                    {
                        url = u.GetString() ?? "";
                        if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                            size = sz.GetInt64();
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(url))
                return new UpdateInfo { Error = $"release 中未找到 {AssetName} 资产" };

            return new UpdateInfo
            {
                HasUpdate = RegistryService.CompareVersions(version, currentVersion) > 0,
                Version = version,
                DownloadUrl = url,
                Size = size,
                Body = body ?? "",
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo { Error = ex.Message };
        }
    }

    /// <summary>
    /// 从 tag 解析语义化版本：容忍 v/V 前缀与前后杂文本（取首个数字起始的点分串），无法解析返回 null。
    /// </summary>
    public static string? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        int start = -1;
        for (int i = 0; i < t.Length; i++)
            if (char.IsDigit(t[i])) { start = i; break; }
        if (start < 0) return null;
        var sb = new StringBuilder();
        for (int i = start; i < t.Length; i++)
        {
            char c = t[i];
            if (char.IsDigit(c) || (c == '.' && sb.Length > 0 && sb[^1] != '.')) sb.Append(c);
            else break;
        }
        var v = sb.ToString().Trim('.');
        var parts = v.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit)) ? v : null;
    }

    /// <summary>带进度下载到临时目录，返回新 exe 路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        string dest = Path.Combine(Path.GetTempPath(), DownloadedExeName);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QxBgmManager");
        using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? info.Size;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            progress?.Report((done, total));
        }
        return dest;
    }

    /// <summary>
    /// 自更新：生成 cmd 脚本（等本进程退出 → 备份旧 exe → 替换 → 重启 → 自删）并启动，
    /// 随后关闭本应用。返回错误信息（未执行）；成功返回 null。
    /// </summary>
    public static string? ApplyAndRestart(string newExePath)
    {
        if (IsFrameworkDependentRun)
            return "当前为框架依赖/调试运行（dotnet *.dll），跳过自替换；请用 git pull 更新后重新构建。";
        string exePath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法定位当前进程 exe 路径");
        if (!File.Exists(newExePath))
            return "下载的新 exe 不存在：" + newExePath;

        string scriptPath = Path.Combine(Path.GetTempPath(), ScriptFileName);
        File.WriteAllText(scriptPath, BuildUpdateScript(Environment.ProcessId, exePath, newExePath));
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + scriptPath + "\"")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
        });
        // 留给脚本几秒完成进程检测，然后关闭本应用（脚本会等待本 PID 退出再替换）
        _ = Task.Delay(500).ContinueWith(_ =>
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()));
        return null;
    }

    /// <summary>生成自更新 cmd 脚本（ASCII 内容，避免代码页问题）。</summary>
    public static string BuildUpdateScript(int pid, string exePath, string newExePath)
    {
        string bakPath = exePath + ".bak";
        return $"""
                @echo off
                rem QxBgmManager self-update: wait for PID {pid} to exit, backup, replace, restart, self-delete
                :wait
                tasklist /FI "PID eq {pid}" | findstr "{pid}" >nul
                if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)
                rem backup old exe, then replace
                if exist "{exePath}" copy /y "{exePath}" "{bakPath}" >nul
                copy /y "{newExePath}" "{exePath}" >nul
                del /q "{newExePath}" >nul
                rem restart app and delete this script
                start "" "{exePath}"
                del /q "%~f0"
                """;
    }
}
