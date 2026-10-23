using System.Diagnostics;
using System.IO;
using System.Text.Json;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>设置读写 + 游戏根目录探测 + 工作区定位。</summary>
public static class SettingsService
{
    public static AppSettings Current { get; set; } = new();

    /// <summary>appsettings.json 位于工作区 app\ 下（跟随工程，不随 bin 目录漂移）。</summary>
    public static string SettingsFile => Path.Combine(FindWorkspaceRoot(), "app", "appsettings.json");

    public static string DefaultFfmpegPath => @"D:\ffmpeg\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe";

    /// <summary>本机已确认的游戏安装（游戏根目录，不含 YuanShen_Data 那一级）。</summary>
    public static readonly string[] KnownGameRoots =
    [
        @"F:\Program Files\Genshin Impact\Genshin Impact Game",
        @"F:\Program Files\Genshin Impact\hk4e_bilibili",
    ];

    /// <summary>从 exe 所在目录向上找含 tools\ptadpcm_enc.py 的目录；找不到回退工程默认路径。</summary>
    public static string FindWorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "ptadpcm_enc.py")))
                return dir.FullName;
        }
        return @"D:\ProgramSource2\QxBgmManager";
    }

    public static string ToolsDir => Path.Combine(FindWorkspaceRoot(), "tools");
    /// <summary>工作产物目录：编码输出、试听临时文件。</summary>
    public static string ResearchDir => Path.Combine(FindWorkspaceRoot(), "build", "bgm-inject-research");
    public static string ImportDir => Path.Combine(ResearchDir, "import");
    public static string PreviewDir => Path.Combine(ResearchDir, "preview");

    // ---- 游戏根目录 ----

    public static string BeyondUgcDir(string gameRoot)
        => Path.Combine(gameRoot, "YuanShen_Data", "StreamingAssets", "AudioAssets", "BeyondUGC");

    public static bool IsValidGameRoot(string? gameRoot)
        => !string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(BeyondUgcDir(gameRoot));

    /// <summary>自动探测已知安装中 BeyondUGC 目录实际存在的。</summary>
    public static List<string> DetectGameRoots() => KnownGameRoots.Where(IsValidGameRoot).ToList();

    /// <summary>8 个 BeydMusic pck 的实际存在路径。</summary>
    public static List<string> MusicPckPaths(string gameRoot)
    {
        var dir = BeyondUgcDir(gameRoot);
        var list = new List<string>(8);
        for (int i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, $"BeydMusic{i}.pck");
            if (File.Exists(p)) list.Add(p);
        }
        return list;
    }

    // ---- 持久化 ----

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (s != null) return s;
            }
        }
        catch { /* 损坏时回退默认 */ }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 只读目录等场景忽略 */ }
    }

    // ---- 依赖解析 ----

    /// <summary>在 PATH 上查找可执行文件（where 语义）。</summary>
    public static string ProbeOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return "";
    }

    /// <summary>解析 python 可执行文件：配置路径有效则用之，否则回退 PATH 上的 python.exe。</summary>
    public static string ResolvePython()
    {
        var p = Current.PythonPath;
        if (string.IsNullOrWhiteSpace(p)) p = "python";
        if (File.Exists(p)) return p;
        if (p.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("python.exe", StringComparison.OrdinalIgnoreCase))
        {
            var probed = ProbeOnPath("python.exe");
            if (!string.IsNullOrEmpty(probed)) return probed;
        }
        return p;
    }

    /// <summary>解析 ffmpeg：配置路径有效则用之，否则回退默认安装路径。</summary>
    public static string ResolveFfmpeg()
    {
        var p = Current.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        if (File.Exists(DefaultFfmpegPath)) return DefaultFfmpegPath;
        return p;
    }

    /// <summary>运行一个短命令验证依赖，返回 (成功, 输出摘要)。</summary>
    public static async Task<(bool Ok, string Output)> RunProbeAsync(string exe, string args)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return (false, "路径不存在：" + exe);
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var text = (stdout + stderr).Trim();
            if (text.Length > 300) text = text[..300];
            return (p.ExitCode == 0, p.ExitCode == 0 ? (text.Length > 0 ? text : "OK") : $"退出码 {p.ExitCode}: {text}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
