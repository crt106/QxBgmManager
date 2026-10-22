using System.Buffers.Binary;
using System.IO;

namespace QxBgmManager.Services;

/// <summary>安装前校验声道、帧边界及索引；与 tools/wem_validate.py 保持一致。</summary>
internal static class WemValidationService
{
    internal sealed record Info(int Channels, uint SampleRate, int DataOffset, int DataSize);

    internal static Task<Info> ValidateFileAsync(string path, CancellationToken ct = default)
        => Task.Run(() => Validate(File.ReadAllBytes(path), ct), ct);

    internal static Info Validate(ReadOnlySpan<byte> raw, CancellationToken ct = default)
    {
        static InvalidDataException Invalid(string message) => new(message);
        ct.ThrowIfCancellationRequested();
        if (raw.Length < 12 || !raw[..4].SequenceEqual("RIFF"u8) || !raw.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw Invalid("音频格式无效，请重新转码");
        if ((long)BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) + 8 != raw.Length)
            throw Invalid("音频文件不完整，请重新转码");
        int fmtOffset = -1, fmtSize = 0, dataOffset = -1, dataSize = 0, loopOffset = -1, loopSize = 0;
        for (int pos = 12; pos < raw.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (raw.Length - pos < 8) throw Invalid("音频区块不完整，请重新转码");
            var tag = raw.Slice(pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(raw[(pos + 4)..]);
            long next = pos + 8L + size + (size & 1);
            if (next > raw.Length) throw Invalid("音频区块越界，请重新转码");
            if (tag.SequenceEqual("fmt "u8))
            {
                if (fmtOffset >= 0) throw Invalid("音频区块重复，请重新转码");
                fmtOffset = pos + 8; fmtSize = (int)size;
            }
            else if (tag.SequenceEqual("data"u8))
            {
                if (dataOffset >= 0) throw Invalid("音频区块重复，请重新转码");
                dataOffset = pos + 8; dataSize = (int)size;
            }
            else if (tag.SequenceEqual("smpl"u8))
            {
                if (loopOffset >= 0) throw Invalid("音频区块重复，请重新转码");
                loopOffset = pos + 8; loopSize = (int)size;
            }
            pos = (int)next;
        }
        if (fmtOffset < 0 || dataOffset < 0) throw Invalid("音频缺少格式或数据，请重新转码");
        if (fmtSize != 24 || fmtOffset > dataOffset) throw Invalid("音频格式头无效，请重新转码");
        var fmt = raw.Slice(fmtOffset, fmtSize);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
        uint average = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
        ushort align = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(fmt) != 0x8311 ||
            BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]) != 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) != 6 ||
            BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]) != 0)
            throw Invalid("仅支持标准 PTADPCM 音频，请重新转码");
        if (channels is not (1 or 2)) throw Invalid("仅支持单声道或双声道音频");
        if (BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]) != (channels == 1 ? 0x4101u : 0x3102u))
            throw Invalid("音频声道信息不一致，请重新转码或获取修复版方案");
        if (rate == 0 || align != 36 * channels || average != (ulong)rate * align / 64)
            throw Invalid("音频帧参数无效，请重新转码");
        if (dataSize == 0 || dataSize % align != 0) throw Invalid("音频数据帧不完整，请重新转码");
        for (int pos = dataOffset + 4; pos < dataOffset + dataSize; pos += 36)
        {
            if ((pos - dataOffset - 4) % (36 * 8192) == 0) ct.ThrowIfCancellationRequested();
            if (raw[pos] > 11) throw Invalid("音频数据帧无效，请重新转码");
        }
        if (loopOffset >= 0)
        {
            if (loopSize < 36) throw Invalid("音频循环信息无效，请重新转码");
            var loop = raw.Slice(loopOffset, loopSize);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(loop[28..]);
            uint extra = BinaryPrimitives.ReadUInt32LittleEndian(loop[32..]);
            if (count > 1 || loopSize != 36L + count * 24L + extra)
                throw Invalid("音频循环信息无效，请重新转码");
            if (count == 1 && (BinaryPrimitives.ReadUInt32LittleEndian(loop[40..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(loop[44..]) > BinaryPrimitives.ReadUInt32LittleEndian(loop[48..]) ||
                BinaryPrimitives.ReadUInt32LittleEndian(loop[48..]) >= (long)dataSize / align * 64))
                throw Invalid("音频循环区间无效，请重新转码");
        }
        return new(channels, rate, dataOffset, dataSize);
    }
}
