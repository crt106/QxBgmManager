using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>只读取音频信息，不做整曲转码；批量添加时限制探测并发。</summary>
public static class AudioMetadataService
{
    private static readonly SemaphoreSlim Gate = new(2, 2);

    public static async Task<AudioMetadata> ReadAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long size = new FileInfo(path).Length;
        await Gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            string ffmpeg = SettingsService.ResolveFfmpeg();
            string probe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (!File.Exists(probe)) probe = SettingsService.ProbeOnPath("ffprobe.exe");
            if (File.Exists(probe))
            {
                var (code, output) = await ProbeAsync(probe,
                    ["-v", "error", "-show_entries", "format=duration:stream=duration", "-of", "json", path], timeout.Token);
                if (code == 0 && ParseProbeDuration(output) is { } duration) return new(duration, size);
            }
            if (File.Exists(ffmpeg))
            {
                // ffprobe 缺失或容器未提供时长时，用同一套 ffmpeg 读取文件头；没有输出文件。
                var (_, output) = await ProbeAsync(ffmpeg, ["-hide_banner", "-i", path], timeout.Token);
                return new(ParseFfmpegDuration(output), size);
            }
            return new(null, size);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, size); }
        catch (IOException) { return new(null, size); }
        catch (System.ComponentModel.Win32Exception) { return new(null, size); }
        finally { Gate.Release(); }
    }

    internal static double? ParseProbeDuration(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var duration) && ParseSeconds(duration) is { } value) return value;
            if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                var values = streams.EnumerateArray().Where(s => s.TryGetProperty("duration", out _))
                    .Select(s => ParseSeconds(s.GetProperty("duration"))).Where(v => v.HasValue).ToList();
                if (values.Count > 0) return values.Max();
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private static double? ParseSeconds(JsonElement value)
        => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
           && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;

    internal static double? ParseFfmpegDuration(string text)
    {
        var match = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
            + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null;
    }

    private static async Task<(int Code, string Output)> ProbeAsync(string exe, string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var registration = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string text = await output + "\n" + await error;
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, text);
    }
}
