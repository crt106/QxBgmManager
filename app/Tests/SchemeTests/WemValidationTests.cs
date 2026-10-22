using System.Buffers.Binary;
using QxBgmManager.Models;
using QxBgmManager.Services;

internal static class WemValidationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        byte[] mono = TestAudioFixture.Create(1, 2), stereo = TestAudioFixture.Create(2, 2);
        check(WemValidationService.Validate(mono).Channels == 1, "单声道配置与帧边界有效");
        check(WemValidationService.Validate(stereo).Channels == 2, "双声道配置与帧边界有效");
        byte[] Change(byte[] source, int offset, byte value) { var b = source.ToArray(); b[offset] = value; return b; }
        byte[] Size(byte[] source) { BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(4), (uint)source.Length - 8); return source; }
        var cases = new Dictionary<string, byte[]>
        {
            ["原崩溃文件头"] = Change(Change(mono, 40, 2), 41, 0x31),
            ["双声道配单声道扩展"] = Change(Change(stereo, 40, 1), 41, 0x41),
            ["旧掩码格式"] = Change(Change(mono, 40, 4), 41, 0),
            ["不支持的编码"] = Change(mono, 20, 1),
            ["帧长度错误"] = Change(mono, 32, 72),
            ["平均速率错误"] = Change(mono, 28, 0),
            ["扩展长度错误"] = Change(mono, 36, 0),
            ["头部截断"] = mono[..30],
            ["数据截断"] = mono[..^1],
            ["长度溢出"] = Change(mono, 63, 255),
            ["首帧索引越界"] = Change(mono, 68, 12),
            ["末帧索引越界"] = Change(mono, 104, 255),
            ["重复格式区块"] = Size([.. mono, .. mono[12..44]]),
            ["重复数据区块"] = Size([.. mono, .. mono[56..]]),
            ["尾部残缺区块"] = Size([.. mono, 0]),
        };
        foreach (var (name, bytes) in cases)
        {
            bool rejected = false;
            try { WemValidationService.Validate(bytes); } catch (InvalidDataException) { rejected = true; }
            check(rejected, $"拒绝{name}");
        }
        // 合法未知奇数长度区块带填充，可安全跳过。
        byte[] padded = Size([.. mono[..44], (byte)'t', (byte)'e', (byte)'s', (byte)'t', 1, 0, 0, 0, 42, 0, .. mono[44..]]);
        check(WemValidationService.Validate(padded).Channels == 1, "未知区块按长度与奇数字节填充跳过");
        using var fixture = new SyntheticPckFixture();
        string invalidPath = Path.Combine(fixture.DirectoryPath, "bad.wem");
        File.WriteAllBytes(invalidPath, cases["原崩溃文件头"]);
        bool blocked = false;
        try { PckService.Patch(fixture.PckPath, 123, invalidPath); } catch (InvalidDataException) { blocked = true; }
        check(blocked && File.ReadAllBytes(fixture.PckPath).SequenceEqual(fixture.Original) &&
            !File.Exists(PckService.BackupPath(fixture.PckPath)), "最终写包入口拒绝错误声道，且不产生备份或写入");
        var manifest = new SchemeManifest { Id = "test-invalid-header", Name = "错误声道测试", Tracks = [
            new() { Target = new() { FileId = 123 }, Audio = new() { Kind = "wem", Uri = invalidPath } }] };
        blocked = false;
        try { await SchemeService.ResolveAudioAsync(manifest, manifest.Tracks[0], null, null, default); }
        catch (SchemeException ex) { blocked = ex.Message.Contains("声道"); }
        check(blocked, "导入音频时提示声道不一致和修复方法");
        string zipPath = Path.Combine(fixture.DirectoryPath, "invalid.qxbgmzip");
        manifest.Scheme = SchemeService.SchemeVersion;
        manifest.Tracks[0].Audio.Uri = "audio/bad.wem";
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
            using var output = zip.CreateEntry("audio/bad.wem").Open();
            output.Write(cases["原崩溃文件头"]);
        }
        blocked = false;
        try { SchemeService.LoadManifestFile(zipPath); } catch (SchemeException) { blocked = true; }
        check(blocked, "打开方案包时即拒绝旧崩溃头，不显示为可安装");
        manifest.Tracks[0].Audio.Uri = invalidPath;
        blocked = false;
        try { using var prepared = await SchemeInstallService.PrepareAsync(manifest, manifest.Tracks,
            PckService.ListEntries(fixture.PckPath), null, null, null, false, _ => { }, (_, _, _) => { }, default); }
        catch (SchemeException) { blocked = true; }
        check(blocked && File.ReadAllBytes(fixture.PckPath).SequenceEqual(fixture.Original), "方案准备阶段拒绝旧错误音频，音乐包保持原状");
    }
}
