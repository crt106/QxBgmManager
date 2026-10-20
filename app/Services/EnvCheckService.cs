using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QxBgmManager.Services;

public enum EnvCheckStatus { Pending, Running, Pass, Warn, Fail }
public enum EnvCheckLevel { Critical, Optional }
public enum EnvCheckFix { None, OpenSettings }

/// <summary>环境检查项（级别、状态、说明、修复动作）。</summary>
public partial class EnvCheckItem : ObservableObject
{
    public string Name { get; init; } = "";
    public EnvCheckLevel Level { get; init; }
    public EnvCheckFix Fix { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    private EnvCheckStatus _status = EnvCheckStatus.Pending;
    [ObservableProperty] private string _detail = "待检查";
    public bool CanFix => Fix == EnvCheckFix.OpenSettings && Status is EnvCheckStatus.Fail or EnvCheckStatus.Warn;
}

/// <summary>首次启动闸门：逐项异步环境检查。关键项(●)全过才能进入主菜单。</summary>
public static class EnvCheckService
{
    public static List<EnvCheckItem> CreateItems() =>
    [
        new() { Name = "游戏根目录", Level = EnvCheckLevel.Critical, Fix = EnvCheckFix.OpenSettings },
        new() { Name = "曲库读写", Level = EnvCheckLevel.Critical, Fix = EnvCheckFix.OpenSettings },
        new() { Name = "Python 可用", Level = EnvCheckLevel.Critical, Fix = EnvCheckFix.OpenSettings },
        new() { Name = "音频编码组件", Level = EnvCheckLevel.Critical, Fix = EnvCheckFix.OpenSettings },
        new() { Name = "ffmpeg 可用", Level = EnvCheckLevel.Critical, Fix = EnvCheckFix.OpenSettings },
        new() { Name = "曲目信息", Level = EnvCheckLevel.Critical },
        new() { Name = "WebView2 运行时（奇域浏览页）", Level = EnvCheckLevel.Optional },
        new() { Name = ".NET 运行时", Level = EnvCheckLevel.Optional },
    ];

    /// <summary>顺序执行全部检查；每项开始/结束都会通过 onUpdate 回调（用于 UI 逐项刷新）。</summary>
    public static async Task RunAllAsync(IReadOnlyList<EnvCheckItem> items, Action<EnvCheckItem>? onUpdate, CancellationToken ct)
    {
        var checks = new (int Index, Func<CancellationToken, Task<(EnvCheckStatus, string, EnvCheckFix? Fix)>> Check)[]
        {
            (0, CheckGameRootAsync),
            (1, CheckWritableAsync),
            (2, CheckPythonAsync),
            (3, CheckNumpyAsync),
            (4, CheckFfmpegAsync),
            (5, CheckTrackMapAsync),
            (6, CheckWebView2Async),
            (7, CheckRuntimeAsync),
        };
        foreach (var (index, check) in checks)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[index];
            item.Status = EnvCheckStatus.Running;
            item.Detail = "检查中…";
            onUpdate?.Invoke(item);
            try
            {
                var (status, detail, fix) = await check(ct);
                item.Status = status;
                item.Detail = detail;
                if (fix != null) item.Fix = fix.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = EnvCheckStatus.Fail;
                item.Detail = "检查异常：" + ex.Message;
            }
            onUpdate?.Invoke(item);
        }
    }

    public static bool AllCriticalPassed(IEnumerable<EnvCheckItem> items)
        => items.Where(i => i.Level == EnvCheckLevel.Critical)
                .All(i => i.Status is EnvCheckStatus.Pass or EnvCheckStatus.Warn);

    // ---- 各检查项 ----

    /// <summary>● 游戏根目录：存在且含 BeyondUGC 目录、8/8 BeydMusic pck 齐全。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckGameRootAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        var root = SettingsService.Current.GameRoot;
        if (string.IsNullOrWhiteSpace(root))
            return (EnvCheckStatus.Fail, "未设置游戏根目录", EnvCheckFix.OpenSettings);
        if (!Directory.Exists(root))
            return (EnvCheckStatus.Fail, "目录不存在：" + root, EnvCheckFix.OpenSettings);
        if (!SettingsService.IsValidGameRoot(root))
            return (EnvCheckStatus.Fail, "未找到 YuanShen_Data\\StreamingAssets\\AudioAssets\\BeyondUGC", EnvCheckFix.OpenSettings);
        int pcks = SettingsService.MusicPckPaths(root).Count;
        return pcks == 8
            ? (EnvCheckStatus.Pass, "已找到完整曲库", null)
            : (EnvCheckStatus.Fail, "游戏音乐文件不完整，请修复游戏", EnvCheckFix.OpenSettings);
    }

    /// <summary>● 曲库目录可写（patch 会原位写 pck）。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckWritableAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        var root = SettingsService.Current.GameRoot;
        if (!SettingsService.IsValidGameRoot(root))
            return (EnvCheckStatus.Fail, "游戏根目录无效，无法测试", EnvCheckFix.OpenSettings);
        string dir = SettingsService.BeyondUgcDir(root);
        return CheckDirWritable(dir)
            ? (EnvCheckStatus.Pass, "可正常读写", null)
            : (EnvCheckStatus.Fail, "无法写入，请检查文件夹权限", EnvCheckFix.OpenSettings);
    }

    /// <summary>目录可写自测：创建后立即删除临时文件。</summary>
    public static bool CheckDirWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            string tmp = Path.Combine(dir, ".qxbgm-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, "x");
            File.Delete(tmp);
            return true;
        }
        catch { return false; }
    }

    /// <summary>● Python 可用：运行 python 打印版本并解析。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckPythonAsync(CancellationToken ct)
    {
        var (ok, output) = await SettingsService.RunProbeAsync(
            SettingsService.ResolvePython(), "-c \"import sys;print(sys.version)\"");
        if (!ok)
            return (EnvCheckStatus.Fail, "无法运行 python：" + output, EnvCheckFix.OpenSettings);
        string? version = ParsePythonVersion(output);
        return version != null
            ? (EnvCheckStatus.Pass, "Python " + version, null)
            : (EnvCheckStatus.Fail, "输出异常：" + output, EnvCheckFix.OpenSettings);
    }

    /// <summary>从 python 输出解析版本号（如 3.13.7）；无法解析返回 null（自测覆盖）。</summary>
    public static string? ParsePythonVersion(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.Length >= 5 && char.IsDigit(line[0]) && line.Contains('.'))
            {
                int end = 0;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.')) end++;
                var v = line[..end].Trim('.');
                if (v.Split('.').Length >= 2) return v;
            }
        }
        return null;
    }

    /// <summary>● 编码依赖：python 从 vendor\python-deps 导入 numpy（路径按工作区解析）。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckNumpyAsync(CancellationToken ct)
    {
        string deps = Path.Combine(SettingsService.FindWorkspaceRoot(), "vendor", "python-deps");
        string code = $"import sys;sys.path.insert(0,r'{deps}');import numpy;print(numpy.__version__)";
        var (ok, output) = await SettingsService.RunProbeAsync(
            SettingsService.ResolvePython(), $"-c \"{code}\"");
        return ok
            ? (EnvCheckStatus.Pass, "可用", null)
            : (EnvCheckStatus.Fail, "编码组件缺失，请检查运行环境", EnvCheckFix.OpenSettings);
    }

    /// <summary>● ffmpeg 可用：配置路径存在且 -version 可执行。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckFfmpegAsync(CancellationToken ct)
    {
        var path = SettingsService.ResolveFfmpeg();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (EnvCheckStatus.Fail, "ffmpeg 不存在：" + (path == "" ? "(未配置)" : path), EnvCheckFix.OpenSettings);
        var (ok, output) = await SettingsService.RunProbeAsync(path, "-version");
        return ok
            ? (EnvCheckStatus.Pass, "可用", null)
            : (EnvCheckStatus.Fail, "ffmpeg 无法执行：" + output, EnvCheckFix.OpenSettings);
    }

    /// <summary>● 曲名映射：registry 共享缓存或本地研究目录文件任一存在即可；缺失给警告级。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckTrackMapAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        // 来源 1：索引仓库 config/track-map.json 的缓存副本
        if (AppState.SharedTrackMap is { Count: > 0 } shared)
            return (EnvCheckStatus.Pass, $"已载入 {shared.Count} 首曲目信息", null);
        // 来源 2：本地研究目录 track-map.json
        var file = TrackMapService.MapFile;
        if (!File.Exists(file))
            return (EnvCheckStatus.Warn, "暂缺曲名，可前往方案市场刷新", null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var tracks = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tracks", out var t) ? t
                : default;
            int count = tracks.ValueKind == JsonValueKind.Array ? tracks.GetArrayLength() : 0;
            return count > 0
                ? (EnvCheckStatus.Pass, $"已载入 {count} 首曲目信息", null)
                : (EnvCheckStatus.Warn, "本地文件存在但没有条目", null);
        }
        catch (Exception)
        {
            return (EnvCheckStatus.Warn, "曲目信息读取失败，请刷新方案市场", null);
        }
    }

    /// <summary>○ WebView2 运行时：缺失仅影响奇域浏览页（警告级）。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckWebView2Async(CancellationToken ct)
    {
        await Task.CompletedTask;
        return HasWebView2Runtime()
            ? (EnvCheckStatus.Pass, "已安装", null)
            : (EnvCheckStatus.Warn, "未检测到 WebView2 运行时，奇域浏览页将降级为提示文本", null);
    }

    public static bool HasWebView2Runtime()
    {
        const string subKey = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            if (k?.GetValue("pv") is string pv && pv != "" && pv != "0.0.0.0") return true;
            using var cu = Registry.CurrentUser.OpenSubKey(subKey);
            return cu?.GetValue("pv") is string pv2 && pv2 != "" && pv2 != "0.0.0.0";
        }
        catch { return false; }
    }

    /// <summary>○ .NET 运行时信息展示（总是通过）。</summary>
    public static async Task<(EnvCheckStatus, string, EnvCheckFix?)> CheckRuntimeAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        return (EnvCheckStatus.Pass, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, null);
    }
}
