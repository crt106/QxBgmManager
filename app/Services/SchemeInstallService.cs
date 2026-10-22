using System.IO;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>所有方案入口共用准备、逐曲登记和可重试恢复流程。</summary>
internal static class SchemeInstallService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal sealed record PreparedTrack(PckEntry Entry, string WemPath, string Sha256, bool AlreadyInstalled);
    internal sealed class PreparedSet(string directory) : IDisposable
    {
        public List<PreparedTrack> Tracks { get; } = new();
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public static async Task<PreparedSet> PrepareAsync(SchemeManifest manifest, IEnumerable<SchemeTrack> tracks,
        IReadOnlyList<PckEntry> library, string? zip, string? baseUri, InstalledScheme? previous, bool upgrading,
        Action<string> log, Action<int, string, EncodeProgress?> report, CancellationToken ct)
    {
        var selected = tracks.ToList();
        var targets = selected.Select(t => SchemeService.FindTarget(t, library)
            ?? throw new SchemeException($"未找到原曲 {t.Target.FileId}")).ToList();
        if (targets.Select(e => (e.PckPath.ToUpperInvariant(), e.FileId)).Distinct().Count() != targets.Count)
            throw new SchemeException("方案中有重复的目标曲目，请调整后重试");
        // 先核对整批冲突，避免下载、编码后才发现不可安装。
        foreach (var e in targets)
        {
            var b = PckService.ReadBackup(e.PckPath)?.Entries.FirstOrDefault(b => b.FileId == e.FileId);
            if (b != null && !Owned(previous, e, b)) throw new SchemeException($"原曲 {e.FileId} 已被其他任务替换，请先恢复");
        }
        string dir = Path.Combine(SettingsService.ResearchDir, "prepared", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var set = new PreparedSet(dir);
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = selected[i]; var e = targets[i];
                var b = PckService.ReadBackup(e.PckPath)?.Entries.FirstOrDefault(b => b.FileId == e.FileId);
                if (!upgrading && b != null && Owned(previous, e, b))
                {
                    if (!PckService.IsReplacementComplete(e.PckPath, b))
                        throw new SchemeException($"原曲 {e.FileId} 上次写入未完成，请先恢复再安装");
                    set.Tracks.Add(new(e, "", b.ReplacementSha256 ?? "", true));
                    report(i, "已安装", null);
                    continue;
                }
                report(i, "正在准备音频…", null);
                string source = await SchemeService.ResolveAudioAsync(manifest, t, zip, null, ct, baseUri);
                string wem = Path.Combine(dir, i + ".wem");
                uint capacity = b?.Size ?? PckService.GetEntry(e.PckPath, e.FileId).Size;
                if (t.Audio.Kind == SchemeService.KindRaw)
                {
                    var metadata = await AudioMetadataService.ReadAsync(source, ct);
                    if (AudioMetadata.EstimateWemBytes(metadata.DurationSeconds) is { } estimated && estimated > capacity)
                        throw new SchemeException($"原曲 {e.FileId} 容量不足，请缩短音频或更换原曲");
                    int rc = await new EncodeService().EncodeAsync(source, wem, PresetService.HzForPreset(t.Encode?.Preset),
                        log, ct, t.Encode?.Loop ?? false, p => report(i, p.DisplayText, p));
                    if (rc != 0) throw new SchemeException("编码失败，请检查音频后重试");
                }
                else await EncodeCache.CopyAtomicAsync(source, wem, ct);
                using (var input = File.OpenRead(wem))
                {
                    byte[] header = new byte[12];
                    if (input.Read(header) != 12 || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                        throw new SchemeException("音频格式无效，请重新选择方案");
                }
                if (new FileInfo(wem).Length > capacity) throw new SchemeException($"原曲 {e.FileId} 容量不足，请缩短音频或更换原曲");
                set.Tracks.Add(new(e, wem, await EncodeCache.HashFileAsync(wem, ct), false));
                report(i, "音频已就绪", null);
            }
            return set;
        }
        catch { set.Dispose(); throw; }
    }

    private static bool Owned(InstalledScheme? scheme, PckEntry entry, BackupEntry backup)
        => scheme?.Tracks.Any(t => t.FileId == entry.FileId &&
            string.Equals(t.PckPath, entry.PckPath, StringComparison.OrdinalIgnoreCase) &&
            (t.WemSha256 == null || string.Equals(t.WemSha256, backup.ReplacementSha256, StringComparison.OrdinalIgnoreCase))) == true;

    public static async Task InstallAsync(InstalledScheme record, PreparedSet prepared,
        Action<int, string> report, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // 重试或补装保留此前已成功的曲目。
            var previous = InstallRegistryService.Load().FirstOrDefault(s => s.Id == record.Id);
            record.Tracks = previous?.Tracks.ToList() ?? new();
            record.Status = "安装未完成";
            InstallRegistryService.Upsert(record);
            try
            {
                for (int i = 0; i < prepared.Tracks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = prepared.Tracks[i]; var e = t.Entry;
                    if (t.AlreadyInstalled)
                    {
                        var active = PckService.ReadBackup(e.PckPath)?.Entries.FirstOrDefault(b => b.FileId == e.FileId);
                        if (active == null || !Owned(record, e, active) || !await Task.Run(() => PckService.IsReplacementComplete(e.PckPath, active), ct))
                            throw new SchemeException("已安装曲目状态发生变化，请重新检查方案");
                        report(i, "完成"); continue;
                    }
                    if (await EncodeCache.HashFileAsync(t.WemPath, ct) != t.Sha256)
                        throw new SchemeException("准备好的音频发生变化，请重新安装");
                    var item = new InstalledTrack { PckPath = e.PckPath, PckName = e.PckName, FileId = e.FileId, WemSha256 = t.Sha256,
                        OriginalSha256 = await Task.Run(() => PckService.EntryHash(e.PckPath, e.FileId), ct) };
                    record.Tracks.RemoveAll(p => p.PckPath == item.PckPath && p.FileId == item.FileId);
                    record.Tracks.Add(item);
                    // 先落登记，再写包；进程中断后仍能找到未完成的恢复入口。
                    InstallRegistryService.Upsert(record);
                    report(i, "正在替换…");
                    try { await Task.Run(() => PckService.Patch(e.PckPath, e.FileId, t.WemPath), ct); }
                    catch
                    {
                        var backup = PckService.ReadBackup(e.PckPath)?.Entries.FirstOrDefault(b => b.FileId == e.FileId);
                        if (backup == null || !string.Equals(backup.ReplacementSha256, t.Sha256, StringComparison.OrdinalIgnoreCase))
                            record.Tracks.Remove(item);
                        throw;
                    }
                    report(i, "完成");
                }
                record.Status = "已安装";
                record.LastError = "";
            }
            catch (Exception ex)
            {
                record.LastError = ex is OperationCanceledException ? "安装已取消，可继续安装或恢复" : ex.Message;
                throw;
            }
            finally { InstallRegistryService.Upsert(record); AppState.InvalidateLibrary(); }
        }
        finally { Gate.Release(); }
    }

    public static async Task<bool> UninstallAsync(InstalledScheme record, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            record = InstallRegistryService.Load().FirstOrDefault(s => s.Id == record.Id) ?? record;
            record.Status = "恢复未完成";
            var errors = new List<string>();
            InstallRegistryService.Upsert(record);
            try
            {
                foreach (var t in record.Tracks.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!File.Exists(t.PckPath)) throw new IOException("音乐包不存在");
                        var b = PckService.ReadBackup(t.PckPath)?.Entries.FirstOrDefault(b => b.FileId == t.FileId);
                        if (b != null)
                        {
                            if (t.WemSha256 != null && !string.Equals(t.WemSha256, b.ReplacementSha256, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("曲目已由其他任务替换，请在替换中心处理");
                            await Task.Run(() => PckService.RestoreOne(t.PckPath, t.FileId), ct);
                        }
                        else if (t.OriginalSha256 == null || await Task.Run(() => PckService.EntryHash(t.PckPath, t.FileId), ct) != t.OriginalSha256)
                            throw new IOException("缺少原曲备份，暂时无法确认恢复结果");
                        record.Tracks.Remove(t);
                        InstallRegistryService.Upsert(record);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add($"{t.FileId}：{ex.Message}"); }
                }
            }
            finally
            {
                record.LastError = string.Join("；", errors);
                if (record.Tracks.Count == 0) InstallRegistryService.Remove(record.Id);
                else InstallRegistryService.Upsert(record);
                AppState.InvalidateLibrary();
            }
            return record.Tracks.Count == 0;
        }
        finally { Gate.Release(); }
    }
}
