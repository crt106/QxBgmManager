using QxBgmManager.Models;
using QxBgmManager.Services;

internal static class EncodeServiceTests
{
    public static async Task Run(Action<bool, string> check)
    {
        check(EncodeProgress.TryParse("QXBGM_PROGRESS {\"stage\":\"encode\",\"percent\":42.5,\"elapsed\":2,\"remaining\":3}", out var parsed)
            && parsed!.Percent == 42.5 && parsed.DisplayText.Contains("预计还需"), "进度协议解析为中文百分比、耗时与预计剩余时间");
        check(!EncodeProgress.TryParse("普通日志", out _) && !EncodeProgress.TryParse("QXBGM_PROGRESS {}", out _), "普通日志及损坏进度不影响转码");
        var settings = SettingsService.Load();
        SettingsService.Current.PythonPath = settings.PythonPath;
        SettingsService.Current.FfmpegPath = settings.FfmpegPath;
        if (!File.Exists(SettingsService.ResolvePython()) || !File.Exists(SettingsService.ResolveFfmpeg()))
        {
            Console.WriteLine("SKIP  未配置 Python/FFmpeg，跳过真实转码集成检查");
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "qxbgm-encode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? previousCache = EncodeCache.DirectoryOverride;
        EncodeCache.DirectoryOverride = Path.Combine(dir, "cache");
        try
        {
            string source = Path.Combine(dir, "测试音频.wav"), wem = Path.Combine(dir, "完成.wem");
            const int frames = 48000 * 2;
            using (var writer = new BinaryWriter(File.Create(source)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
                writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(frames * 4);
                for (int i = 0; i < frames; i++)
                {
                    writer.Write((short)(20000 * Math.Sin(i * 440 * Math.PI * 2 / 48000)));
                    writer.Write((short)(17000 * Math.Sin(i * 660 * Math.PI * 2 / 48000)));
                }
            }
            var encoder = new EncodeService();
            var progress = new List<EncodeProgress>();
            var logs = new List<string>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int rc = await encoder.EncodeAsync(source, wem, 6000, line => logs.Add(line), default,
                loop: true, onProgress: p => progress.Add(p));
            double coldMs = timer.Elapsed.TotalMilliseconds;
            check(rc == 0 && File.Exists(wem), "真实 Python/FFmpeg 低通与循环编码完成（中文路径）");
            check(progress.Count > 4 && progress.Any(p => p.Percent > 0 && p.Percent < 100)
                && progress.Last().Percent == 100, "真实子进程持续上报中间进度并到达 100%");
            check(progress.Zip(progress.Skip(1)).All(p => p.First.Percent <= p.Second.Percent), "转码百分比单调递增");
            check(!logs.Any(l => l.StartsWith("QXBGM_PROGRESS")), "机器进度不混入用户日志");
            progress.Clear();
            string reused = Path.Combine(dir, "复用.wem");
            timer.Restart();
            rc = await encoder.EncodeAsync(source, reused, 6000, _ => { }, default, loop: true, onProgress: p => progress.Add(p));
            Console.WriteLine($"INFO  2 秒合成双声道音频：首次编码 {coldMs:F1} ms，缓存复用 {timer.Elapsed.TotalMilliseconds:F1} ms");
            check(rc == 0 && progress.Any(p => p.Stage == "cached") && File.ReadAllBytes(wem).SequenceEqual(File.ReadAllBytes(reused)),
                "真实编码产物可复用，复制结果逐字节一致且明确显示缓存命中");
            string decoded = Path.Combine(dir, "试听.wav");
            rc = await encoder.DecodeToWavAsync(wem, decoded, _ => { }, default);
            check(rc == 0 && new FileInfo(decoded).Length >= 44 + frames * 4, "批量解码与 WAV 导出完成");

            using var cts = new CancellationTokenSource();
            string canceled = Path.Combine(dir, "取消.wem");
            bool wasCanceled = false;
            try
            {
                await encoder.EncodeAsync(source, canceled, 0, _ => { }, cts.Token, onProgress: p =>
                { if (p.Stage == "encode" && p.Percent > 5) cts.Cancel(); });
            }
            catch (OperationCanceledException) { wasCanceled = true; }
            check(wasCanceled && !File.Exists(canceled) && !encoder.IsRunning, "中途取消终止进程树，不遗留可误用的半成品");
            rc = await encoder.DecodeToWavAsync(wem, decoded, _ => { }, default);
            check(rc == 0, "取消后释放工作队列，后续任务正常执行");
        }
        finally { EncodeCache.DirectoryOverride = previousCache; Directory.Delete(dir, true); }
    }
}
