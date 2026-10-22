using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QxBgmManager.Services;

/// <summary>按音频内容、编码器版本及参数复用产物；校验通过的完整文件才可命中。</summary>
internal static class EncodeCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    internal static string? DirectoryOverride { get; set; }
    internal static string DirectoryPath => DirectoryOverride ?? Path.Combine(SettingsService.ResearchDir, "encode-cache");

    internal static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
    }

    public static async Task<int> GetOrEncodeAsync(string source, string destination, double lowpass, bool loop,
        Func<string, Task<int>> encode, Action<string> onHit, CancellationToken ct)
    {
        // 持有源文件只读共享句柄，避免散列与编码期间源音频被改写。
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sourceHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        string scriptHash = await HashFileAsync(Path.Combine(SettingsService.ToolsDir, "ptadpcm_enc.py"), ct);
        string validatorHash = await HashFileAsync(Path.Combine(SettingsService.ToolsDir, "wem_validate.py"), ct);
        string Identity(string path) => File.Exists(path)
            ? $"{Path.GetFullPath(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}" : path;
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"qxbgm-cache-2|{sourceHash}|{scriptHash}|{validatorHash}|48000|2|64|{lowpass.ToString("R", CultureInfo.InvariantCulture)}|{loop}|" +
            Identity(SettingsService.ResolvePython()) + "|" + Identity(SettingsService.ResolveFfmpeg()))));
        Directory.CreateDirectory(DirectoryPath);
        string cached = Path.Combine(DirectoryPath, key + ".wem");
        var gate = Gates.GetOrAdd(cached, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            bool valid = File.Exists(cached) && File.Exists(cached + ".sha256") &&
                await HashFileAsync(cached, ct) == (await File.ReadAllTextAsync(cached + ".sha256", ct)).Trim();
            if (valid)
            {
                try { await WemValidationService.ValidateFileAsync(cached, ct); }
                catch (InvalidDataException) { valid = false; }
            }
            if (!valid)
            {
                string temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    int rc = await encode(temporary);
                    if (rc != 0 || !File.Exists(temporary)) return rc == 0 ? 1 : rc;
                    ct.ThrowIfCancellationRequested();
                    await WemValidationService.ValidateFileAsync(temporary, ct);
                    string digest = await HashFileAsync(temporary, ct);
                    File.Move(temporary, cached, true);
                    await File.WriteAllTextAsync(cached + ".sha256", digest, ct);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else onHit("已复用转码结果");
            ct.ThrowIfCancellationRequested();
            await CopyAtomicAsync(cached, destination, ct);
            return 0;
        }
        finally { gate.Release(); }
    }

    internal static async Task CopyAtomicAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
