using System.IO;
using System.Security.Cryptography;
using System.Text;
using QxBgmManager.Models;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Services;

public static class TrackPreviewService
{
    private static CancellationTokenSource? _playingRequest;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string CachePath(PckEntry entry, bool original, string extension)
    {
        var file = new FileInfo(entry.PckPath);
        string identity = $"{file.FullName.ToUpperInvariant()}|{(file.Exists ? file.Length : 0)}|{file.LastWriteTimeUtc.Ticks}|{entry.FileId}|{original}";
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(SettingsService.PreviewDir, key + extension);
    }

    public static void Stop()
    {
        _playingRequest?.Cancel();
        AudioPreviewService.Stop();
    }

    public static async Task PlayAsync(TrackRow row, bool original, CancellationToken ct)
    {
        Stop();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        request.CancelAfter(TimeSpan.FromMinutes(3));
        _playingRequest = request;
        try
        {
            string path = await PrepareAsync(row.Entry, original, request.Token);
            request.Token.ThrowIfCancellationRequested();
            AudioPreviewService.Play(path);
        }
        finally { if (ReferenceEquals(_playingRequest, request)) _playingRequest = null; }
    }

    internal static async Task<string> PrepareAsync(PckEntry entry, bool original, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        string wav = CachePath(entry, original, ".wav"), wem = Path.ChangeExtension(wav, ".wem");
        string tempWav = wav + "." + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            if (!File.Exists(wav))
            {
                await Task.Run(() =>
                {
                    var backup = original ? PckService.ReadBackup(entry.PckPath)?.Entries.FirstOrDefault(b => b.FileId == entry.FileId) : null;
                    Directory.CreateDirectory(SettingsService.PreviewDir);
                    if (backup?.OrigWemFile is { } name && Path.GetFileName(name) == name)
                        File.Copy(Path.Combine(Path.GetDirectoryName(entry.PckPath)!, name), wem, true);
                    else PckService.ExtractEntry(entry.PckPath, entry.FileId, wem);
                }, ct);
                if (wav != CachePath(entry, original, ".wav")) throw new IOException("音乐已变化，请重新试听");
                int rc = await new EncodeService().DecodeToWavAsync(wem, tempWav, _ => { }, ct, preview: true);
                if (rc != 0) throw new IOException("试听准备失败，请重试");
                ct.ThrowIfCancellationRequested();
                if (wav != CachePath(entry, original, ".wav")) throw new IOException("音乐已变化，请重新试听");
                File.Move(tempWav, wav, true);
            }
            return wav;
        }
        finally
        {
            if (File.Exists(tempWav)) File.Delete(tempWav);
            Gate.Release();
        }
    }
}
