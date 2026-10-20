using System.Diagnostics;
using System.IO;
using System.Text;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// 编码/解码子进程封装（保留 Python 子进程调用）：
/// encode = python tools\ptadpcm_enc.py encode（低通与 PCM 读取共用一次 ffmpeg），
/// decode = python tools\ptadpcm_enc.py decode。onLine 在后台线程回调，需自行切 UI 线程。
/// </summary>
public class EncodeService
{
    private Process? _process;
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    public bool IsRunning => _process is { HasExited: false };

    public Task<int> EncodeAsync(string sourcePath, string outWem, double lowPassHz,
        Action<string> onLine, CancellationToken ct, bool loop = false, Action<EncodeProgress>? onProgress = null)
        => RunEncodeAsync(sourcePath, outWem, lowPassHz, onLine, ct, loop, onProgress);

    private async Task<int> RunEncodeAsync(string sourcePath, string outWem, double lowPassHz,
        Action<string> onLine, CancellationToken ct, bool loop, Action<EncodeProgress>? onProgress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outWem))!);
        var encPy = Path.Combine(SettingsService.ToolsDir, "ptadpcm_enc.py");
        string loopArg = loop ? " --loop" : "";
        string filterArg = lowPassHz > 0 ? $" --lowpass {lowPassHz.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        return await RunAsync(SettingsService.ResolvePython(),
            $"-u {Q(encPy)} encode {Q(sourcePath)} {Q(outWem)} --channels 2 --beam 64 --progress{loopArg}{filterArg}",
            onLine, ct, onProgress);
    }

    /// <summary>wem → wav（独立帧批量解码），产物供 MediaPlayer 试听。</summary>
    public Task<int> DecodeToWavAsync(string wemPath, string outWav,
        Action<string> onLine, CancellationToken ct, Action<EncodeProgress>? onProgress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outWav))!);
        var encPy = Path.Combine(SettingsService.ToolsDir, "ptadpcm_enc.py");
        return RunAsync(SettingsService.ResolvePython(),
            $"-u {Q(encPy)} decode {Q(wemPath)} {Q(outWav)} --progress", onLine, ct, onProgress);
    }

    /// <summary>wav → mp3（libmp3lame -q:a 4）。</summary>
    public Task<int> ConvertToMp3Async(string wavPath, string outMp3,
        Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outMp3))!);
        return RunAsync(SettingsService.ResolveFfmpeg(),
            $"-y -hide_banner -i {Q(wavPath)} -codec:a libmp3lame -q:a 4 {Q(outMp3)}", onLine, ct);
    }

    private async Task<int> RunAsync(string exe, string args, Action<string> onLine, CancellationToken ct,
        Action<EncodeProgress>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(exe))
            throw new FileNotFoundException("未找到可执行文件，请在设置页配置依赖路径。");

        onProgress?.Invoke(new EncodeProgress("wait", 0, 0, 0));
        await WorkerGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
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

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = process;
            process.Start();

            await using var reg = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            var stdoutTask = PumpAsync(process.StandardOutput, line =>
            {
                if (EncodeProgress.TryParse(line, out var progress)) onProgress?.Invoke(progress!);
                else onLine(line);
            });
            var stderrTask = PumpAsync(process.StandardError, line => onLine(line));
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally { _process = null; WorkerGate.Release(); }
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
