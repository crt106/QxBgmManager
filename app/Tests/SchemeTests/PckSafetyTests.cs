using System.Buffers.Binary;
using System.Text;
using QxBgmManager.Services;

internal static class PckSafetyTests
{
    public static void Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "qxbgm-pck-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string pck = Path.Combine(dir, "test.pck"), wem = Path.Combine(dir, "new.wem");
            var original = new byte[1024];
            new Random(12).NextBytes(original);
            Encoding.ASCII.GetBytes("AKPK").CopyTo(original, 0);
            original.AsSpan(12, 8).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(28), 2);
            for (int i = 0; i < 2; i++)
            {
                var lut = original.AsSpan(32 + i * 20, 20);
                BinaryPrimitives.WriteUInt32LittleEndian(lut, (uint)(123 + i));
                BinaryPrimitives.WriteUInt32LittleEndian(lut[4..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(lut[8..], 256);
                BinaryPrimitives.WriteUInt64LittleEndian(lut[12..], (ulong)(128 + i * 384));
            }
            File.WriteAllBytes(pck, original);
            byte[] replacement = new byte[64];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(replacement, 0);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(replacement, 8);
            File.WriteAllBytes(wem, replacement);
            string backup = PckService.BackupPath(pck);

            byte[] ReadLive()
            {
                using var file = new FileStream(pck, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] data = new byte[file.Length];
                file.ReadExactly(data);
                return data;
            }

            try
            {
                PckService.PatchWithCheckpoint(pck, 123, wem, stage =>
                {
                    if (stage != "backup-saved") return;
                    check(ReadLive().SequenceEqual(original), "备份提交时 PCK 尚未修改");
                    var record = PckService.ReadBackup(pck)!.Entries.Single();
                    check(File.ReadAllBytes(Path.Combine(dir, record.OrigWemFile!))
                        .SequenceEqual(original.AsSpan(128, 256).ToArray()), "首次覆盖前已保存完整原始字节及索引清单");
                    throw new IOException("模拟备份后中断");
                });
            }
            catch (IOException) { }
            PckService.RestoreOne(pck, 123);
            check(File.ReadAllBytes(pck).SequenceEqual(original), "备份后中断可幂等恢复");

            try
            {
                PckService.PatchWithCheckpoint(pck, 123, wem, stage =>
                { if (stage == "payload-written") throw new IOException("模拟音频写入后中断"); });
            }
            catch (IOException) { }
            check(PckService.ReadBackup(pck)!.Entries.Count == 1, "音频覆盖后中断仍保留恢复记录");
            PckService.RestoreOne(pck, 123);
            check(File.ReadAllBytes(pck).SequenceEqual(original), "音频覆盖后中断可逐字节恢复");

            Directory.CreateDirectory(backup);
            try { PckService.Patch(pck, 123, wem); check(false, "清单提交失败应中止"); }
            catch (IOException) { check(File.ReadAllBytes(pck).SequenceEqual(original), "清单提交失败不修改 PCK"); }
            catch (UnauthorizedAccessException) { check(File.ReadAllBytes(pck).SequenceEqual(original), "清单提交失败不修改 PCK"); }
            Directory.Delete(backup);
            check(!File.Exists(backup + ".123.bin"), "备份提交失败清理本次临时数据");

            Task.WaitAll(Task.Run(() => PckService.Patch(pck, 123, wem)), Task.Run(() => PckService.Patch(pck, 124, wem)));
            check(PckService.ReadBackup(pck)!.Entries.Count == 2, "同包并发替换保留两条独立备份");
            PckService.RestoreOne(pck, 123);
            check(PckService.GetEntry(pck, 123).Size == 256 && PckService.GetEntry(pck, 124).Size == 64,
                "单条恢复不影响另一条替换");
            PckService.RestoreAll(pck);
            check(File.ReadAllBytes(pck).SequenceEqual(original) && !File.Exists(backup), "全部恢复与原始 PCK 逐字节一致");

            PckService.Patch(pck, 123, wem);
            byte[] patched = File.ReadAllBytes(pck);
            string bin = backup + ".123.bin";
            byte[] saved = File.ReadAllBytes(bin);
            File.WriteAllBytes(bin, saved[..16]);
            try { PckService.RestoreOne(pck, 123); check(false, "不完整备份应拒绝恢复"); }
            catch (InvalidDataException) { check(File.ReadAllBytes(pck).SequenceEqual(patched) && File.Exists(backup), "不完整备份不触碰 PCK，保留登记"); }
            File.WriteAllBytes(bin, saved);
            PckService.RestoreAll(pck);
        }
        finally { Directory.Delete(dir, true); }
    }
}
