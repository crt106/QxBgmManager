using System.Diagnostics;
using System.Text.Json;
using QxBgmManager.Models;
using QxBgmManager.Services;
using QxBgmManager.ViewModels;

internal static class WorkflowTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var audio = new TestAudioFixture();
        using var fixture = new SyntheticPckFixture();
        string pck = fixture.PckPath, bp = PckService.BackupPath(pck);
        byte[] Read() => File.ReadAllBytes(pck);
        void Rejected(Action action, string message)
        {
            byte[] before = Read(); bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && Read().SequenceEqual(before) && File.Exists(bp), message);
        }
        PckService.Patch(pck, 123, audio.WemPath);
        var backup = PckService.ReadBackup(pck)!;
        check(backup.OriginalPckSha256?.Length == 64 && backup.PckLength == fixture.Original.Length,
            "新备份保存原包指纹、大小及音频校验值");
        string originalFile = Path.Combine(fixture.DirectoryPath, backup.Entries[0].OrigWemFile!);
        byte[] originalAudio = File.ReadAllBytes(originalFile);
        byte[] corrupt = originalAudio.ToArray(); corrupt[15] ^= 1;
        File.WriteAllBytes(originalFile, corrupt);
        Rejected(() => PckService.RestoreOne(pck, 123), "同长度的损坏备份也拒绝恢复，保留清单且不写包");
        File.WriteAllBytes(originalFile, originalAudio);
        byte[] patched = Read();
        corrupt = patched.ToArray(); corrupt[^1] ^= 1; File.WriteAllBytes(pck, corrupt);
        Rejected(() => PckService.RestoreOne(pck, 123), "游戏更新改动非替换区段时拒绝旧备份");
        File.WriteAllBytes(pck, patched);
        corrupt = patched.ToArray(); corrupt[44] ^= 1; File.WriteAllBytes(pck, corrupt);
        Rejected(() => PckService.RestoreOne(pck, 123), "原曲偏移变化时拒绝写入");
        File.WriteAllBytes(pck, patched);
        corrupt = patched.ToArray(); corrupt[128 + 16] = (byte)(fixture.Original[128 + 16] ^ 128);
        if (corrupt[144] == patched[144]) corrupt[144] ^= 64;
        File.WriteAllBytes(pck, corrupt);
        Rejected(() => PckService.RestoreOne(pck, 123), "替换区段被外部改动时拒绝写入");
        File.WriteAllBytes(pck, patched);
        // 模拟原地写入中断及恢复写到一半，备份保持有效。
        fixture.Original.AsSpan(128 + 20, 1000).CopyTo(patched.AsSpan(128 + 20));
        File.WriteAllBytes(pck, patched);
        PckService.RestoreOne(pck, 123);
        check(Read().SequenceEqual(fixture.Original), "原始与替换字节混合的中断状态仍可完整恢复");

        string python = SettingsService.ResolvePython();
        async Task<int> Python(string command, params string[] args)
        {
            var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add(Path.Combine(SettingsService.ToolsDir, "bgm_swap.py"));
            psi.ArgumentList.Add(command); psi.ArgumentList.Add(pck);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        if (File.Exists(python))
        {
            PckService.Patch(pck, 123, audio.WemPath);
            check(await Python("restore") == 0 && Read().SequenceEqual(fixture.Original), "Python 可校验并恢复 C# 新格式备份");
            check(await Python("patch", "123", audio.WemPath) == 0, "Python 新格式替换成功");
            corrupt = Read(); corrupt[^1] ^= 1; File.WriteAllBytes(pck, corrupt);
            check(await Python("restore") != 0 && Read().SequenceEqual(corrupt), "Python 同样拒绝音乐包版本变化，保持原文件");
            corrupt[^1] ^= 1; File.WriteAllBytes(pck, corrupt);
            PckService.RestoreOne(pck, 123);
            check(Read().SequenceEqual(fixture.Original), "C# 可校验并恢复 Python 新格式备份");
            int rc;
            using (var exclusive = new FileStream(pck, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                rc = await Python("patch", "123", audio.WemPath);
            check(rc != 0 && Read().SequenceEqual(fixture.Original), "Python 写入与 C# 写句柄互斥，竞争失败不修改音乐包");
        }
        else Console.WriteLine("SKIP 未配置 Python，跳过双实现互认检查");

        var rows = PckService.ListEntries(pck).Select(e => new TrackRow(e, null, false)).ToList();
        var first = new ReplaceJob { SourcePath = audio.WemPath, Target = rows[0], SourceDurationSeconds = 0.05 };
        var second = new ReplaceJob { SourcePath = audio.WemPath, Target = rows[0], SourceDurationSeconds = 0.05 };
        check(ReplacementPreflight.Check([first, second]).Count == 2, "重复目标在转码前标记全部冲突任务");
        second.Target = rows[1]; second.SourceDurationSeconds = 120;
        check(ReplacementPreflight.Check([first, second]).Count == 1 && Read().SequenceEqual(fixture.Original), "超容量在转码前拦截，不修改音乐包");
        second.SourcePath = Path.Combine(fixture.DirectoryPath, "missing.wav");
        check(ReplacementPreflight.Check([second])[second].Contains("源文件不存在"), "源文件丢失有直接修复引导");
        check(first.WemPath != new ReplaceJob { SourcePath = audio.WemPath }.WemPath, "同名源文件的任务输出互不覆盖");
        string preview = rows[0].WavPreviewPath;
        File.SetLastWriteTimeUtc(pck, DateTime.UtcNow.AddSeconds(3));
        check(preview != rows[0].WavPreviewPath && rows[0].WavPreviewPath != TrackPreviewService.CachePath(rows[0].Entry, true, ".wav"),
            "试听缓存随音乐包变化失效，并区分原曲与当前音乐");
        long revision = AppState.Revision;
        AppState.InvalidateLibrary();
        check(AppState.Revision > revision, "曲库变更版本独立于共享缓存的已加载标记");

        var oldData = AppDataService.DirectoryOverride;
        var oldCache = EncodeCache.DirectoryOverride;
        AppDataService.DirectoryOverride = Path.Combine(fixture.DirectoryPath, "data");
        EncodeCache.DirectoryOverride = Path.Combine(fixture.DirectoryPath, "cache");
        try
        {
            await CheckCache(check, fixture.DirectoryPath);
            await CheckScheme(check, fixture, audio.WemPath);
        }
        finally { AppDataService.DirectoryOverride = oldData; EncodeCache.DirectoryOverride = oldCache; }
    }

    private static async Task CheckCache(Action<bool, string> check, string dir)
    {
        string source = Path.Combine(dir, "cache-source.wav"), output = Path.Combine(dir, "cache-output.wem");
        File.WriteAllText(source, "源音频一");
        int calls = 0, hits = 0;
        byte[] validWem = TestAudioFixture.Create();
        Task<int> Encode(string path) { calls++; File.WriteAllBytes(path, validWem); return Task.FromResult(0); }
        Task<int> Run(double lowpass = 0, bool loop = false, CancellationToken ct = default) =>
            EncodeCache.GetOrEncodeAsync(source, output, lowpass, loop, Encode, _ => hits++, ct);
        await Run(); await Run();
        check(calls == 1 && hits == 1, "同一音频和参数重试命中缓存，无第二次编码");
        await Run(6000); await Run(6000, true);
        check(calls == 3, "低通或循环参数变化会重新编码");
        File.WriteAllText(source, "源音频二"); await Run();
        check(calls == 4, "源文件内容变化不会误用旧转码");
        foreach (string file in Directory.GetFiles(EncodeCache.DirectoryPath, "*.wem")) File.WriteAllText(file, "损坏");
        await Run();
        check(calls == 5 && File.ReadAllBytes(output).SequenceEqual(validWem), "损坏缓存自动重新编码，不复制错误产物");
        File.WriteAllText(source, "并发音频");
        await Task.WhenAll(Run(), Run());
        check(calls == 6, "相同音频并发请求只编码一次");
        foreach (string file in Directory.GetFiles(EncodeCache.DirectoryPath, "*.wem"))
        {
            byte[] invalid = validWem.ToArray(); invalid[40] = 1;
            File.WriteAllBytes(file, invalid);
            await File.WriteAllTextAsync(file + ".sha256", await EncodeCache.HashFileAsync(file, default));
        }
        await Run();
        check(calls == 7 && File.ReadAllBytes(output).SequenceEqual(validWem), "散列正确但声道头错误的缓存也会重新编码");
        File.WriteAllText(source, "无效编码输出");
        bool invalidOutputRejected = false;
        try
        {
            await EncodeCache.GetOrEncodeAsync(source, output, 0, false, path =>
            { File.WriteAllText(path, "无效产物"); return Task.FromResult(0); }, _ => { }, default);
        }
        catch (InvalidDataException) { invalidOutputRejected = true; }
        check(invalidOutputRejected && File.ReadAllBytes(output).SequenceEqual(validWem), "编码器返回成功但文件无效时不覆盖已有输出");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false;
        try { await Run(ct: canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "缓存命中也响应取消");
    }

    private static async Task CheckScheme(Action<bool, string> check, SyntheticPckFixture fixture, string wem)
    {
        string pck = fixture.PckPath;
        var manifest = new SchemeManifest { Id = "test-workflow", Name = "合成方案", Tracks = [
            new() { Target = new() { FileId = 123 }, Audio = new() { Kind = "wem", Uri = wem } },
            new() { Target = new() { FileId = 124 }, Audio = new() { Kind = "wem", Uri = wem } }] };
        InstalledScheme Record() => new() { Id = manifest.Id, Name = manifest.Name, InstalledAt = DateTime.Now };
        async Task<SchemeInstallService.PreparedSet> Prepare(bool upgrade = false) => await SchemeInstallService.PrepareAsync(
            manifest, manifest.Tracks, PckService.ListEntries(pck), null, null,
            InstallRegistryService.Load().FirstOrDefault(), upgrade, _ => { }, (_, _, _) => { }, default);
        using (var prepared = await Prepare())
        using (var cancel = new CancellationTokenSource())
        {
            bool canceled = false;
            try { await SchemeInstallService.InstallAsync(Record(), prepared, (i, text) => { if (i == 0 && text == "完成") cancel.Cancel(); }, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            var record = InstallRegistryService.Load().Single();
            check(canceled && record.Tracks.Count == 1 && record.Status == "安装未完成" && PckService.IsEntryPatched(pck, 123),
                "安装一首后取消，逐曲登记保留成功结果和未完成状态");
        }
        using (var prepared = await Prepare())
        {
            check(prepared.Tracks[0].AlreadyInstalled && !prepared.Tracks[1].AlreadyInstalled, "重试识别已成功曲目，只准备剩余曲目");
            await SchemeInstallService.InstallAsync(Record(), prepared, (_, _) => { }, default);
            check(InstallRegistryService.Load().Single().Tracks.Count == 2 && InstallRegistryService.Load().Single().Status == "已安装",
                "继续安装保留旧曲目登记并完成剩余任务");
        }
        byte[] installedBytes = File.ReadAllBytes(pck);
        manifest.Tracks[0].Audio.Sha256 = new string('0', 64);
        bool changedRejected = false;
        try { using var prepared = await Prepare(); } catch (SchemeException) { changedRejected = true; }
        check(changedRejected && File.ReadAllBytes(pck).SequenceEqual(installedBytes), "同 ID 新内容不能误判为旧版本已经安装");
        manifest.Tracks[0].Audio.Sha256 = null;
        manifest.Tracks[1].Audio.Uri = Path.Combine(fixture.DirectoryPath, "missing.wem");
        bool prepareFailed = false;
        try { using var prepared = await Prepare(true); } catch (SchemeException) { prepareFailed = true; }
        check(prepareFailed && File.ReadAllBytes(pck).SequenceEqual(installedBytes) && InstallRegistryService.Load().Single().Tracks.Count == 2,
            "新版准备失败时旧方案及安装登记保持完整");
        string invalid = Path.Combine(fixture.DirectoryPath, "invalid.wem");
        File.WriteAllBytes(invalid, new byte[128]); manifest.Tracks[1].Audio.Uri = invalid;
        bool invalidRejected = false;
        try { using var prepared = await Prepare(true); } catch (SchemeException) { invalidRejected = true; }
        check(invalidRejected && File.ReadAllBytes(pck).SequenceEqual(installedBytes), "新版音频格式无效时在卸载旧方案之前拒绝升级");
        string manifestPath = PckService.BackupPath(pck), movedManifest = manifestPath + ".saved";
        File.Move(manifestPath, movedManifest);
        check(!await SchemeInstallService.UninstallAsync(InstallRegistryService.Load().Single()) && InstallRegistryService.Load().Single().Tracks.Count == 2,
            "备份清单丢失时保留恢复登记，不误判为已恢复");
        File.Move(movedManifest, manifestPath);
        var backup = PckService.ReadBackup(pck)!;
        string bin = Path.Combine(fixture.DirectoryPath, backup.Entries.Single(b => b.FileId == 124).OrigWemFile!);
        byte[] original = File.ReadAllBytes(bin); byte[] bad = original.ToArray(); bad[0] ^= 1; File.WriteAllBytes(bin, bad);
        bool restored = await SchemeInstallService.UninstallAsync(InstallRegistryService.Load().Single());
        check(!restored && InstallRegistryService.Load().Single().Status == "恢复未完成" && InstallRegistryService.Load().Single().Tracks.Count > 0,
            "恢复失败保留未完成登记和错误原因");
        File.WriteAllBytes(bin, original);
        check(await SchemeInstallService.UninstallAsync(InstallRegistryService.Load().Single()) && InstallRegistryService.Load().Count == 0
            && File.ReadAllBytes(pck).SequenceEqual(fixture.Original), "修复备份后可继续恢复，完整恢复后才移除方案登记");
        manifest.Tracks[1].Audio.Uri = wem;
        using (var prepared = await Prepare()) await SchemeInstallService.InstallAsync(Record(), prepared, (_, _) => { }, default);
        PckService.RestoreAll(pck);
        check(await SchemeInstallService.UninstallAsync(InstallRegistryService.Load().Single()) && InstallRegistryService.Load().Count == 0,
            "在替换中心手动恢复后，方案卸载通过原曲散列确认并清理登记");
        // 旧备份仍按结构校验恢复，新字段保持可选。
        PckService.Patch(pck, 123, wem);
        var legacy = PckService.ReadBackup(pck)!;
        File.Delete(Path.Combine(fixture.DirectoryPath, legacy.Entries[0].ReplacementFile!));
        legacy.PckLength = null; legacy.OriginalPckSha256 = null;
        legacy.Entries[0].OrigWemSha256 = null; legacy.Entries[0].ReplacementFile = null; legacy.Entries[0].ReplacementSha256 = null;
        File.WriteAllText(PckService.BackupPath(pck), JsonSerializer.Serialize(legacy));
        PckService.RestoreOne(pck, 123);
        check(File.ReadAllBytes(pck).SequenceEqual(fixture.Original), "旧格式备份仍可在结构校验通过后恢复");
        using var other = new SyntheticPckFixture();
        var partial = new InstalledScheme { Id = "partial-restore", Name = "跨包恢复" };
        foreach (string file in new[] { pck, other.PckPath })
        {
            string originalHash = PckService.EntryHash(file, 123);
            PckService.Patch(file, 123, wem);
            partial.Tracks.Add(new InstalledTrack { PckPath = file, FileId = 123,
                WemSha256 = PckService.ReadBackup(file)!.Entries.Single().ReplacementSha256, OriginalSha256 = originalHash });
        }
        InstallRegistryService.Upsert(partial);
        string otherBin = Path.Combine(other.DirectoryPath, PckService.ReadBackup(other.PckPath)!.Entries.Single().OrigWemFile!);
        byte[] otherOriginal = File.ReadAllBytes(otherBin); byte[] otherBad = otherOriginal.ToArray(); otherBad[0] ^= 1;
        File.WriteAllBytes(otherBin, otherBad);
        check(!await SchemeInstallService.UninstallAsync(partial) && InstallRegistryService.Load().Single().Tracks.Count == 1
            && InstallRegistryService.Load().Single().Tracks[0].PckPath == other.PckPath && File.ReadAllBytes(pck).SequenceEqual(fixture.Original),
            "跨包恢复部分成功时，只保留失败曲目的登记");
        File.WriteAllBytes(otherBin, otherOriginal);
        check(await SchemeInstallService.UninstallAsync(partial) && InstallRegistryService.Load().Count == 0,
            "再次恢复仅处理剩余曲目，成功后清空登记");
    }
}
