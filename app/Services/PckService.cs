using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using QxBgmManager.Models;

namespace QxBgmManager.Services;

/// <summary>
/// BeydMusic pck（Wwise AKPK）读写，bgm_swap.py list/patch/restore/status 语义的 C# 移植。
/// 备份产物 &lt;pck&gt;.bgmbackup（JSON）与 &lt;pck&gt;.bgmbackup.&lt;fileID&gt;.bin 的格式
/// 与 Python 版逐字段一致（fileID/offset/size/lut_pos/orig_wem_b64/orig_wem_file/orig_lut_hex），
/// 两侧可互相识别并复原对方打的补丁。
/// </summary>
public static class PckService
{
    public const int LutEntrySize = 20;
    /// <summary>PTADPCM 48kHz 立体声约 54000 字节/秒，用于估算时长。</summary>
    public const double BytesPerSecond = 54000.0;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static string BackupPath(string pckPath) => pckPath + ".bgmbackup";

    // ---- 头部 / LUT 解析（bgm_swap.py read_header + list_entries）----

    /// <summary>AKPK 头 28 字节；随后依次是语言/声库/流式媒体/外部文件四段 LUT。</summary>
    private static long StmLutOffset(Stream f)
    {
        f.Seek(0, SeekOrigin.Begin);
        Span<byte> h = stackalloc byte[28];
        ReadExact(f, h);
        if (h[0] != (byte)'A' || h[1] != (byte)'K' || h[2] != (byte)'P' || h[3] != (byte)'K')
            throw new InvalidDataException("不是 AKPK pck 文件");
        uint langSize = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        uint sbSize = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        return 28L + langSize + sbSize;
    }

    public static List<PckEntry> ListEntries(string pckPath)
    {
        lock (Gate(pckPath))
        {
        using var f = File.OpenRead(pckPath);
        return ListEntries(f, pckPath);
        }
    }

    private static List<PckEntry> ListEntries(Stream f, string pckPath)
    {
        long stmOff = StmLutOffset(f);
        f.Seek(stmOff, SeekOrigin.Begin);
        uint count = ReadU32(f);
        var entries = new List<PckEntry>((int)Math.Min(count, 1 << 20));
        byte[] raw = new byte[LutEntrySize];
        for (uint i = 0; i < count; i++)
        {
            ReadExact(f, raw);
            entries.Add(new PckEntry
            {
                PckPath = pckPath,
                PckName = Path.GetFileName(pckPath),
                FileId = BinaryPrimitives.ReadUInt32LittleEndian(raw),
                Lang = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)),
                Offset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(12)),
                LutPos = stmOff + 4 + (long)i * LutEntrySize,
            });
        }
        return entries;
    }

    public static PckEntry GetEntry(string pckPath, uint fileId)
        => ListEntries(pckPath).FirstOrDefault(e => e.FileId == fileId)
           ?? throw new InvalidDataException($"fileID {fileId} 不在 {Path.GetFileName(pckPath)} 中");

    /// <summary>抽取条目字节为 .wem 文件（bgm_swap.py extract）。</summary>
    public static void ExtractEntry(string pckPath, uint fileId, string outPath)
    {
        lock (Gate(pckPath))
        {
        var e = GetEntry(pckPath, fileId);
        using var f = File.OpenRead(pckPath);
        f.Seek((long)e.Offset, SeekOrigin.Begin);
        byte[] data = new byte[e.Size];
        ReadExact(f, data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllBytes(outPath, data);
        }
    }

    internal static bool IsReplacementComplete(string pckPath, BackupEntry backup)
    {
        lock (Gate(pckPath))
        {
            if (backup.ReplacementSha256 == null) return true; // 旧格式仅能沿用原有登记。
            var e = GetEntry(pckPath, backup.FileId);
            using var f = File.OpenRead(pckPath);
            f.Position = (long)e.Offset;
            byte[] bytes = new byte[e.Size];
            ReadExact(f, bytes);
            return Hash(bytes).Equals(backup.ReplacementSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string EntryHash(string pckPath, uint fileId)
    {
        lock (Gate(pckPath))
        {
            var e = GetEntry(pckPath, fileId);
            using var f = File.OpenRead(pckPath);
            f.Position = (long)e.Offset;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] bytes = new byte[65536];
            long remaining = e.Size;
            while (remaining > 0)
            {
                int n = (int)Math.Min(bytes.Length, remaining);
                ReadExact(f, bytes.AsSpan(0, n));
                hash.AppendData(bytes, 0, n);
                remaining -= n;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    // ---- 备份读写（status 语义：备份文件存在即视为 patched）----

    public static BgmBackup? ReadBackup(string pckPath)
    {
        string bp = BackupPath(pckPath);
        if (!File.Exists(bp)) return null;
        return JsonSerializer.Deserialize<BgmBackup>(File.ReadAllText(bp));
    }

    public static bool IsPatched(string pckPath) => File.Exists(BackupPath(pckPath));

    public static bool IsEntryPatched(string pckPath, uint fileId)
        => ReadBackup(pckPath)?.Entries.Any(b => b.FileId == fileId) == true;

    // ---- patch ----

    /// <summary>
    /// 原位替换一个条目（bgm_swap.py patch）：新 wem 必须 ≤ 原条目大小；
    /// 被覆盖的原始字节与 LUT 记录先落盘备份。同条目重复 patch 会被拒绝。
    /// </summary>
    public static PckEntry Patch(string pckPath, uint fileId, string wemPath)
        => PatchWithCheckpoint(pckPath, fileId, wemPath, null);

    // 故障注入仅供离线自测，在真实写入边界检查备份与中断恢复。
    internal static PckEntry PatchWithCheckpoint(string pckPath, uint fileId, string wemPath, Action<string>? checkpoint)
    {
        lock (Gate(pckPath))
        {
            var newBytes = File.ReadAllBytes(wemPath);
            if (newBytes.Length < 12 ||
                Encoding.ASCII.GetString(newBytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(newBytes, 8, 4) != "WAVE")
                throw new InvalidDataException("替换文件不是 RIFF/WAVE（.wem）格式");

            // 从读取到写入始终持有同一文件句柄，禁止其他写入者在容量检查后改动文件。
            using var f = new FileStream(pckPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var e = ListEntries(f, pckPath).FirstOrDefault(e => e.FileId == fileId)
                ?? throw new InvalidDataException($"未找到音乐条目 {fileId}");
            if (newBytes.Length > e.Size)
                throw new InvalidDataException(
                    $"替换文件 {newBytes.Length:N0} B 大于条目容量 {e.Size:N0} B，请选更小的编码或更长的原曲");

            string bp = BackupPath(pckPath);
            BgmBackup backup;
            if (File.Exists(bp))
            {
                backup = ReadBackup(pckPath)
                         ?? throw new InvalidDataException($"备份文件损坏：{bp}");
                if (backup.Entries.Any(b => b.FileId == fileId))
                    throw new InvalidOperationException($"fileID {fileId} 已处于补丁状态，请先复原");
                ValidateBackup(f, pckPath, backup);
            }

            else
            {
                backup = new BgmBackup { Pck = Path.GetFullPath(pckPath) };
            }
            backup.PckLength ??= f.Length;
            backup.OriginalPckSha256 ??= CanonicalHash(f, pckPath, backup);

            f.Seek((long)e.Offset, SeekOrigin.Begin);
            byte[] origWem = new byte[e.Size];
            ReadExact(f, origWem);
            f.Seek(e.LutPos, SeekOrigin.Begin);
            byte[] origLut = new byte[LutEntrySize];
            ReadExact(f, origLut);
            string binPath = $"{bp}.{fileId}.bin";
            var record = new BackupEntry
            {
                FileId = e.FileId,
                Offset = e.Offset,
                Size = e.Size,
                LutPos = e.LutPos,
                OrigWemB64 = null,
                OrigWemFile = Path.GetFileName(binPath),
                OrigLutHex = ToHexLower(origLut),
                OrigWemSha256 = Hash(origWem),
                ReplacementFile = Path.GetFileName(binPath + ".new"),
                ReplacementSha256 = Hash(newBytes),
            };
            // 先持久化音频备份，再原子提交清单；任一步失败都不能触碰 PCK。
            WriteAtomic(binPath, origWem, overwrite: false);
            bool replacementSaved = false;
            try
            {
                WriteAtomic(binPath + ".new", newBytes, overwrite: false);
                replacementSaved = true;
                backup.Entries.Add(record);
                SaveBackup(bp, backup);
            }
            catch { File.Delete(binPath); if (replacementSaved) File.Delete(binPath + ".new"); throw; }
            checkpoint?.Invoke("backup-saved");

            // 此后发生写入失败或进程中断，完整备份保留，恢复操作可安全重试。
            f.Seek((long)e.Offset, SeekOrigin.Begin);
            f.Write(newBytes);
            f.Write(new byte[e.Size - newBytes.Length]);
            checkpoint?.Invoke("payload-written");
            BinaryPrimitives.WriteUInt32LittleEndian(origLut.AsSpan(8), (uint)newBytes.Length);
            f.Seek(e.LutPos, SeekOrigin.Begin);
            f.Write(origLut);
            f.Flush(flushToDisk: true);
            AppState.InvalidateLibrary();
            return e;
        }
    }

    // ---- restore ----

    /// <summary>复原一个条目的全部活动补丁（bgm_swap.py restore）。</summary>
    public static void RestoreAll(string pckPath)
    {
        lock (Gate(pckPath))
        {
            var backup = ReadBackup(pckPath)
                         ?? throw new InvalidDataException("没有备份，无需复原");
            foreach (var entry in backup.Entries.ToList()) RestoreOne(pckPath, entry.FileId);
        }
    }

    /// <summary>只复原一个条目，其余补丁保持活动；最后一条复原后删除整个备份文件。</summary>
    public static void RestoreOne(string pckPath, uint fileId)
    {
        lock (Gate(pckPath))
        {
            string bp = BackupPath(pckPath);
            var backup = ReadBackup(pckPath)
                         ?? throw new InvalidDataException("没有备份，无需复原");
            var entry = backup.Entries.FirstOrDefault(b => b.FileId == fileId)
                        ?? throw new InvalidDataException($"备份中没有 fileID {fileId}");
            RestoreEntry(pckPath, backup, entry);
            backup.Entries.Remove(entry);
            if (backup.Entries.Count == 0)
                File.Delete(bp);
            else
                SaveBackup(bp, backup);
            // 清单更新成功后才删除数据，恢复中断时不会留下指向缺失数据的清单。
            DeleteBin(pckPath, entry);
            AppState.InvalidateLibrary();
        }
    }

    private static void RestoreEntry(string pckPath, BgmBackup backup, BackupEntry entry)
    {
        string binPath = DataPath(pckPath, entry.OrigWemFile);
        if (!File.Exists(binPath))
            throw new FileNotFoundException($"备份数据文件缺失：{binPath}");
        byte[] orig = File.ReadAllBytes(binPath);
        byte[] lut = Convert.FromHexString(entry.OrigLutHex ?? "");
        if (lut.Length != LutEntrySize || orig.Length != entry.Size)
            throw new InvalidDataException("备份不完整，无法恢复");
        using var f = new FileStream(pckPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        ValidateBackup(f, pckPath, backup);
        f.Seek((long)entry.Offset, SeekOrigin.Begin);
        f.Write(orig, 0, orig.Length);
        f.Seek(entry.LutPos, SeekOrigin.Begin);
        f.Write(lut, 0, lut.Length);
        f.Flush(flushToDisk: true);
        f.Seek((long)entry.Offset, SeekOrigin.Begin);
        byte[] actual = new byte[orig.Length];
        ReadExact(f, actual);
        if (!actual.AsSpan().SequenceEqual(orig)) throw new IOException("恢复校验失败，请重试");
        f.Seek(entry.LutPos, SeekOrigin.Begin);
        byte[] actualLut = new byte[LutEntrySize];
        ReadExact(f, actualLut);
        if (!actualLut.AsSpan().SequenceEqual(lut)) throw new IOException("恢复校验失败，请重试");
    }

    private static void DeleteBin(string pckPath, BackupEntry entry)
    {
        string binPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pckPath))!,
                                      entry.OrigWemFile ?? "");
        if (File.Exists(binPath)) File.Delete(binPath);
        if (entry.ReplacementFile != null) File.Delete(DataPath(pckPath, entry.ReplacementFile));
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string DataPath(string pck, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("备份文件名无效");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pck))!, name);
    }

    /// <summary>写入前验证包身份及全部备份；允许中断写入留下的原始/替换字节混合。</summary>
    private static void ValidateBackup(Stream f, string pck, BgmBackup backup)
    {
        if (backup.PckLength is { } length && length != f.Length)
            throw new InvalidDataException("音乐包已变化，无法使用这份备份");
        var entries = ListEntries(f, pck);
        foreach (var b in backup.Entries)
        {
            var e = entries.SingleOrDefault(e => e.FileId == b.FileId);
            byte[] lut = Convert.FromHexString(b.OrigLutHex ?? "");
            byte[] orig = File.ReadAllBytes(DataPath(pck, b.OrigWemFile));
            if (e == null || lut.Length != LutEntrySize || orig.Length != b.Size ||
                b.Offset > (ulong)f.Length || b.Size > (ulong)f.Length - b.Offset ||
                e.Offset != b.Offset || e.LutPos != b.LutPos ||
                BinaryPrimitives.ReadUInt32LittleEndian(lut) != b.FileId ||
                BinaryPrimitives.ReadUInt32LittleEndian(lut.AsSpan(4)) != e.Lang ||
                BinaryPrimitives.ReadUInt32LittleEndian(lut.AsSpan(8)) != b.Size ||
                BinaryPrimitives.ReadUInt64LittleEndian(lut.AsSpan(12)) != b.Offset)
                throw new InvalidDataException("音乐包结构与备份不匹配，已停止恢复");
            if (b.OrigWemSha256 != null && !Hash(orig).Equals(b.OrigWemSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原曲备份校验失败，已停止恢复");
            if (b.ReplacementFile != null)
            {
                byte[] replacement = File.ReadAllBytes(DataPath(pck, b.ReplacementFile));
                if (replacement.Length > orig.Length || !Hash(replacement).Equals(b.ReplacementSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("替换记录校验失败，已停止恢复");
                byte[] currentSize = BitConverter.GetBytes(e.Size), newSize = BitConverter.GetBytes((uint)replacement.Length);
                if (Enumerable.Range(0, 4).Any(i => currentSize[i] != lut[8 + i] && currentSize[i] != newSize[i]))
                    throw new InvalidDataException("音乐索引已变化，已停止恢复");
                f.Position = (long)b.Offset;
                byte[] buffer = new byte[65536];
                for (int start = 0; start < orig.Length; start += buffer.Length)
                {
                    int count = Math.Min(buffer.Length, orig.Length - start);
                    ReadExact(f, buffer.AsSpan(0, count));
                    for (int i = 0; i < count; i++)
                    {
                        int index = start + i;
                        byte expected = index < replacement.Length ? replacement[index] : (byte)0;
                        if (buffer[i] != orig[index] && buffer[i] != expected)
                            throw new InvalidDataException("音乐内容已被其他程序改动，已停止恢复");
                    }
                }
            }
            else if (e.Size > b.Size) throw new InvalidDataException("旧备份与当前音乐容量不匹配");
        }
        if (backup.OriginalPckSha256 != null &&
            !CanonicalHash(f, pck, backup).Equals(backup.OriginalPckSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音乐包版本与备份不匹配，已停止恢复");
    }

    // 将已替换区段视作原始字节，得到稳定的原包指纹；流式读取，不加载整个音乐包。
    private static string CanonicalHash(Stream f, string pck, BgmBackup backup)
    {
        var ranges = backup.Entries.SelectMany(b => new[]
        {
            (Offset: b.LutPos, Size: (long)LutEntrySize, File: (string?)null, Bytes: Convert.FromHexString(b.OrigLutHex ?? "")),
            (Offset: (long)b.Offset, Size: (long)b.Size, File: (string?)DataPath(pck, b.OrigWemFile), Bytes: Array.Empty<byte>())
        }).OrderBy(r => r.Offset).ToList();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        void Append(Stream source, long count)
        {
            while (count > 0)
            {
                int n = (int)Math.Min(buffer.Length, count);
                ReadExact(source, buffer.AsSpan(0, n));
                hash.AppendData(buffer, 0, n);
                count -= n;
            }
        }
        f.Position = 0;
        foreach (var r in ranges)
        {
            if (r.Offset < f.Position || r.Offset > f.Length || r.Size > f.Length - r.Offset)
                throw new InvalidDataException("备份区段无效");
            Append(f, r.Offset - f.Position);
            if (r.File == null) hash.AppendData(r.Bytes);
            else { using var original = File.OpenRead(r.File); Append(original, r.Size); }
            f.Position = r.Offset + r.Size;
        }
        Append(f, f.Length - f.Position);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // ---- 小工具 ----

    private static void SaveBackup(string path, BgmBackup backup)
        => WriteAtomic(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, JsonOpts)), overwrite: true);

    private static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ReadExact(Stream f, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = f.Read(buf[read..]);
            if (n == 0) throw new EndOfStreamException("pck 文件读取中途 EOF");
            read += n;
        }
    }

    private static uint ReadU32(Stream f)
    {
        Span<byte> b = stackalloc byte[4];
        ReadExact(f, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    /// <summary>Python bytes.hex() 为小写，保持一致便于人眼比对。</summary>
    private static string ToHexLower(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
