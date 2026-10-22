"""校验可安装的 PTADPCM WEM；只读，不自动修补外部音频。"""
import struct


def channel_config(channels):
    if channels not in (1, 2):
        raise ValueError("仅支持单声道或双声道音频")
    return 0x4101 if channels == 1 else 0x3102


def validate_wem(raw):
    if len(raw) < 12 or raw[:4] != b'RIFF' or raw[8:12] != b'WAVE':
        raise ValueError("音频格式无效，请重新转码")
    if struct.unpack_from('<I', raw, 4)[0] + 8 != len(raw):
        raise ValueError("音频文件不完整，请重新转码")
    chunks = {}; pos = 12
    while pos < len(raw):
        if len(raw) - pos < 8:
            raise ValueError("音频区块不完整，请重新转码")
        tag, size = struct.unpack_from('<4sI', raw, pos)
        start = pos + 8; end = start + size
        if end + (size & 1) > len(raw):
            raise ValueError("音频区块越界，请重新转码")
        if tag in (b'fmt ', b'data', b'smpl'):
            if tag in chunks:
                raise ValueError("音频区块重复，请重新转码")
            chunks[tag] = (start, size)
        pos = end + (size & 1)
    if b'fmt ' not in chunks or b'data' not in chunks:
        raise ValueError("音频缺少格式或数据，请重新转码")
    off, size = chunks[b'fmt ']; data_off, data_size = chunks[b'data']
    if size != 24 or off > data_off:
        raise ValueError("音频格式头无效，请重新转码")
    tag, channels, rate, avg, align, bits, extra, reserved, config = struct.unpack_from('<HHIIHHHHI', raw, off)
    if tag != 0x8311 or bits != 4 or extra != 6 or reserved != 0:
        raise ValueError("仅支持标准 PTADPCM 音频，请重新转码")
    if config != channel_config(channels):
        raise ValueError("音频声道信息不一致，请重新转码或获取修复版方案")
    if rate == 0 or align != 36 * channels or avg != rate * align // 64:
        raise ValueError("音频帧参数无效，请重新转码")
    if data_size == 0 or data_size % align:
        raise ValueError("音频数据帧不完整，请重新转码")
    if any(i > 11 for i in raw[data_off + 4:data_off + data_size:36]):
        raise ValueError("音频数据帧无效，请重新转码")
    samples = data_size // align * 64
    if b'smpl' in chunks:
        off, size = chunks[b'smpl']
        if size < 36:
            raise ValueError("音频循环信息无效，请重新转码")
        loops, extra_bytes = struct.unpack_from('<II', raw, off + 28)
        if loops > 1 or size != 36 + loops * 24 + extra_bytes:
            raise ValueError("音频循环信息无效，请重新转码")
        if loops:
            loop_type, start, end = struct.unpack_from('<III', raw, off + 40)
            if loop_type != 0 or start > end or end >= samples:
                raise ValueError("音频循环区间无效，请重新转码")
    return dict(channels=channels, rate=rate, align=align, data_offset=data_off,
                data_size=data_size, samples=samples, duration_seconds=samples / rate)
