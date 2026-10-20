using System.Diagnostics;
using System.IO;
using System.Text;

namespace QxBgmManager.Services;

/// <summary>
/// 编码/解码子进程封装（保留 Python 子进程调用）：
/// encode = python tools\ptadpcm_enc.py encode（8bit 预设先 ffmpeg 6kHz 低通），
/// decode = python tools\ptadpcm_enc.py decode。onLine 在后台线程回调，需自行切 UI 线程。
/// </summary>
public class EncodeService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public Task<int> EncodeAsync(string sourcePath, string outWem, bool lowPass8Bit,
        Action<string> onLine, CancellationToken ct, bool loop = false)
        => RunEncodeAsync(sourcePath, outWem, lowPass8Bit, onLine, ct, loop);

    private async Task<int> RunEncodeAsync(string sourcePath, string outWem, bool lowPass8Bit,
        Action<string> onLine, CancellationToken ct, bool loop = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outWem))!);
        string input = sourcePath;
        string? tmpWav = null;
        try
        {
            if (lowPass8Bit)
            {
                tmpWav = Path.Combine(SettingsService.ResearchDir, "tmp_lowpass6k.wav");
                onLine("[8bit 预设] ffmpeg 6kHz 低通滤波…");
                int rc = await RunAsync(SettingsService.ResolveFfmpeg(),
                    $"-y -hide_banner -i {Q(sourcePath)} -af lowpass=f=6000 {Q(tmpWav)}", onLine, ct);
                if (rc != 0) throw new InvalidOperationException("ffmpeg 低通滤波失败（退出码 " + rc + "）");
                input = tmpWav;
            }

            var encPy = Path.Combine(SettingsService.ToolsDir, "ptadpcm_enc.py");
            string loopArg = loop ? " --loop" : "";
            return await RunAsync(SettingsService.ResolvePython(),
                $"{Q(encPy)} encode {Q(input)} {Q(outWem)} --channels 2 --beam 64{loopArg}", onLine, ct);
        }
        finally
        {
            if (tmpWav != null && File.Exists(tmpWav))
            {
                try { File.Delete(tmpWav); } catch { }
            }
        }
    }

    /// <summary>wem → wav（约实时速度），产物供 MediaPlayer 试听。</summary>
    public Task<int> DecodeToWavAsync(string wemPath, string outWav,
        Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outWav))!);
        var encPy = Path.Combine(SettingsService.ToolsDir, "ptadpcm_enc.py");
        return RunAsync(SettingsService.ResolvePython(),
            $"{Q(encPy)} decode {Q(wemPath)} {Q(outWav)}", onLine, ct);
    }

    /// <summary>wav → mp3（libmp3lame -q:a 4）。</summary>
    public Task<int> ConvertToMp3Async(string wavPath, string outMp3,
        Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outMp3))!);
        return RunAsync(SettingsService.ResolveFfmpeg(),
            $"-y -hide_banner -i {Q(wavPath)} -codec:a libmp3lame -q:a 4 {Q(outMp3)}", onLine, ct);
    }

    private async Task<int> RunAsync(string exe, string args, Action<string> onLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe))
            throw new FileNotFoundException("未找到可执行文件，请在设置页配置依赖路径。");

        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = SettingsService.FindWorkspaceRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 子脚本 print 中文：强制 UTF-8，避免 GBK 控制台乱码
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // ptadpcm_enc.py 内部解码 PCM 也走 ffmpeg，让子进程用同一配置
        psi.Environment["FFMPEG"] = SettingsService.ResolveFfmpeg();

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Start();

        await using var reg = ct.Register(() =>
        {
            try { _process?.Kill(entireProcessTree: true); } catch { }
        });

        var stdoutTask = PumpAsync(_process.StandardOutput, onLine);
        var stderrTask = PumpAsync(_process.StandardError, line => onLine("[stderr] " + line));
        await _process.WaitForExitAsync(ct).ContinueWith(_ => { }, CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask);
        return _process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line)) onLine(line);
        }
    }

    public static string Q(string path) => "\"" + path + "\"";
}
