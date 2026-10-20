# PTADPCM (Wwise fmt tag 0x8311) 编码器：任意音频 -> 千星奇域音乐库兼容 .wem
# 算法依据：vgmstream src/coding/ptadpcm_decoder.c 的逆向格式
#   帧 = frame_size 字节/声道（立体声 blockalign 72 -> 每声道 36B）
#   帧头: s16 hist2, s16 hist1, u8 index(0..11)
#   之后 62 个 4bit nibble（低位在前）：sample = clamp16(step + 2*hist1 - hist2)
# 用法:
#   python tools/ptadpcm_enc.py encode <输入音频> <输出.wem> [--channels 2] [--loop]
#   python tools/ptadpcm_enc.py selftest <输入音频>     # 编码->自解码->SNR 报告
import argparse
import os
import struct
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "vendor", "python-deps"))
import numpy as np  # noqa: E402

FFMPEG = os.environ.get("FFMPEG", r"D:\ffmpeg\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe")
RATE = 48000
FRAME_PAYLOAD = 31  # 每帧每声道 31 字节 nibble = 62 采样
SAMPLES_PER_FRAME = 64  # 含帧头 2 个原始采样

# vgmstream ptadpcm_table 前 12 行（index 12 全 0，解码端会把 >12 钳到 12）
_TABLE_ROWS = [
    [(-14, 2), (-10, 2), (-7, 1), (-5, 1), (-3, 0), (-2, 0), (-1, 0), (0, 0),
     (0, 0), (1, 0), (2, 0), (3, 0), (5, 1), (7, 1), (10, 2), (14, 2)],
    [(-28, 3), (-20, 3), (-14, 2), (-10, 2), (-7, 1), (-5, 1), (-3, 1), (-1, 0),
     (1, 0), (3, 1), (5, 1), (7, 1), (10, 2), (14, 2), (20, 3), (28, 3)],
    [(-56, 4), (-40, 4), (-28, 3), (-20, 3), (-14, 2), (-10, 2), (-6, 2), (-2, 1),
     (2, 1), (6, 2), (10, 2), (14, 2), (20, 3), (28, 3), (40, 4), (56, 4)],
    [(-112, 5), (-80, 5), (-56, 4), (-40, 4), (-28, 3), (-20, 3), (-12, 3), (-4, 2),
     (4, 2), (12, 3), (20, 3), (28, 3), (40, 4), (56, 4), (80, 5), (112, 5)],
    [(-224, 6), (-160, 6), (-112, 5), (-80, 5), (-56, 4), (-40, 4), (-24, 4), (-8, 3),
     (8, 3), (24, 4), (40, 4), (56, 4), (80, 5), (112, 5), (160, 6), (224, 6)],
    [(-448, 7), (-320, 7), (-224, 6), (-160, 6), (-112, 5), (-80, 5), (-48, 5), (-16, 4),
     (16, 4), (48, 5), (80, 5), (112, 5), (160, 6), (224, 6), (320, 7), (448, 7)],
    [(-896, 8), (-640, 8), (-448, 7), (-320, 7), (-224, 6), (-160, 6), (-96, 6), (-32, 5),
     (32, 5), (96, 6), (160, 6), (224, 6), (320, 7), (448, 7), (640, 8), (896, 8)],
    [(-1792, 9), (-1280, 9), (-896, 8), (-640, 8), (-448, 7), (-320, 7), (-192, 7), (-64, 6),
     (64, 6), (192, 7), (320, 7), (448, 7), (640, 8), (896, 8), (1280, 9), (1792, 9)],
    [(-3584, 10), (-2560, 10), (-1792, 9), (-1280, 9), (-896, 8), (-640, 8), (-384, 8), (-128, 7),
     (128, 7), (384, 8), (640, 8), (896, 8), (1280, 9), (1792, 9), (2560, 10), (3584, 10)],
    [(-7168, 11), (-5120, 11), (-3584, 10), (-2560, 10), (-1792, 9), (-1280, 9), (-768, 9), (-256, 8),
     (256, 8), (768, 9), (1280, 9), (1792, 9), (2560, 10), (3584, 10), (5120, 11), (7168, 11)],
    [(-14336, 11), (-10240, 11), (-7168, 11), (-5120, 11), (-3584, 10), (-2560, 10), (-1536, 10), (-512, 9),
     (512, 9), (1536, 10), (2560, 10), (3584, 10), (5120, 11), (7168, 11), (10240, 11), (14336, 11)],
    [(-28672, 11), (-20480, 11), (-14336, 11), (-10240, 11), (-7168, 11), (-5120, 11), (-3072, 11), (-1024, 10),
     (1024, 10), (3072, 11), (5120, 11), (7168, 11), (10240, 11), (14336, 11), (20480, 11), (28672, 11)],
]
STEP_TAB = np.array([[s for s, _ in row] for row in _TABLE_ROWS] + [[0] * 16], dtype=np.int32)
NEXT_TAB = np.array([[n for _, n in row] for row in _TABLE_ROWS] + [[0] * 16], dtype=np.int32)


def load_pcm(path, channels, rate=RATE):
    r = subprocess.run([
        FFMPEG, "-v", "error", "-i", path, "-ar", str(rate), "-ac", str(channels),
        "-f", "s16le", "-",
    ], capture_output=True)
    if r.returncode != 0:
        sys.exit("ffmpeg decode failed: " + r.stderr.decode("utf-8", "replace"))
    pcm = np.frombuffer(r.stdout, dtype=np.int16)
    return pcm.reshape(-1, channels).astype(np.int32)  # (N, ch)


MAX_STEP = np.abs(STEP_TAB[:, 0])  # 每个 index 的最大步长


def pick_start_index(seg):
    # 帧内最大二阶变化 -> 选能一步覆盖它的最小 index
    d = np.abs(seg[2:] - 2 * seg[1:-1] + seg[:-2]).max() if len(seg) > 2 else 0
    for idx in range(12):
        if MAX_STEP[idx] >= d * 0.5:
            return idx
    return 11


def encode_channel(samples, heuristic_idx=True, lookahead=0):
    n_frames = (len(samples) + SAMPLES_PER_FRAME - 1) // SAMPLES_PER_FRAME
    pad = n_frames * SAMPLES_PER_FRAME - len(samples)
    if pad:
        samples = np.concatenate([samples, np.zeros(pad, dtype=np.int32)])
    out = np.empty(n_frames * 36, dtype=np.uint8)
    for f in range(n_frames):
        seg = samples[f * SAMPLES_PER_FRAME:(f + 1) * SAMPLES_PER_FRAME]
        h2, h1 = int(seg[0]), int(seg[1])
        frame = bytearray(36)
        struct.pack_into("<hhB", frame, 0,
                         max(-32768, min(32767, h2)),
                         max(-32768, min(32767, h1)), 0)
        idx = pick_start_index(seg) if heuristic_idx else 0
        for i in range(2, SAMPLES_PER_FRAME):
            t = int(seg[i])
            pred = np.clip(STEP_TAB[idx] + 2 * h1 - h2, -32768, 32767)
            err = np.abs(pred - t)
            if lookahead >= 1 and i + 1 < SAMPLES_PER_FRAME:
                t2 = int(seg[i + 1])
                nidx = NEXT_TAB[idx]  # (16,)
                h1v = pred          # (16,)
                # 第二层: 对每个候选 nibble 的新状态, 对 t2 的最优误差
                pred2 = np.clip(STEP_TAB[nidx] + 2 * h1v[:, None] - h1, -32768, 32767)
                err = err + np.abs(pred2 - t2).min(axis=1) * 0.5
            nib = int(np.argmin(err))
            s = int(pred[nib])
            idx = int(NEXT_TAB[idx][nib])
            if i & 1:
                frame[5 + (i - 2) // 2] |= nib << 4
            else:
                frame[5 + (i - 2) // 2] |= nib
            h2, h1 = h1, s
        out[f * 36:(f + 1) * 36] = frame
    return out


def encode_channel_beam(samples, beam=64, ef=0.0):
    """beam search 近似最优编码：束宽 B 的状态 (累计误差,h1,h2,idx,路径) 逐采样扩展。
    ef>0 时开启误差反馈：把上一步量化误差的一部分带进当前目标，压低感知噪声底。"""
    n_frames = (len(samples) + SAMPLES_PER_FRAME - 1) // SAMPLES_PER_FRAME
    pad = n_frames * SAMPLES_PER_FRAME - len(samples)
    if pad:
        samples = np.concatenate([samples, np.zeros(pad, dtype=np.int32)])
    B = beam
    out = np.empty(n_frames * 36, dtype=np.uint8)
    for f in range(n_frames):
        seg = samples[f * SAMPLES_PER_FRAME:(f + 1) * SAMPLES_PER_FRAME]
        h2, h1 = int(seg[0]), int(seg[1])
        frame = bytearray(36)
        struct.pack_into("<hhB", frame, 0,
                         max(-32768, min(32767, h2)),
                         max(-32768, min(32767, h1)), 0)
        # 初始束: 铺满 12 个起始 index 让搜索自选, 微小 jitter 破平局防退化
        b_err = np.arange(B, dtype=np.float64) * 1e-3
        b_h1 = np.full(B, h1, dtype=np.int32)
        b_h2 = np.full(B, h2, dtype=np.int32)
        b_idx = (np.arange(B) % 12).astype(np.int64)
        b_idx0 = b_idx.copy()  # 记录每条束的起始 index, 最终写进帧头
        b_ef = np.zeros(B, dtype=np.float64)
        b_path = np.zeros((B, 62), dtype=np.uint8)
        for i in range(62):
            t = int(seg[i + 2])
            step = STEP_TAB[b_idx]                       # (B,16)
            pred = np.clip(step + 2 * b_h1[:, None] - b_h2[:, None], -32768, 32767)
            t_eff = t + ef * b_ef                        # (B,)
            total = b_err[:, None] + np.abs(pred - t_eff[:, None])  # (B,16)
            flat = total.ravel()
            keep = np.argpartition(flat, B)[:B]
            keep = keep[np.argsort(flat[keep])]
            src, nib = keep // 16, keep % 16
            chosen = pred.ravel()[keep]
            b_ef = t - chosen                            # 对真实目标的误差反馈
            b_err = total.ravel()[keep]
            b_h2 = b_h1[src]
            b_h1 = chosen
            b_idx = NEXT_TAB[b_idx[src], nib]
            b_idx0 = b_idx0[src]
            b_path = b_path[src]
            b_path[:, i] = nib
        best = b_path[0]
        frame[4] = int(b_idx0[0])  # 胜出束的起始 index 写入帧头
        for n in range(62):
            if n & 1:
                frame[5 + n // 2] |= int(best[n]) << 4
            else:
                frame[5 + n // 2] |= int(best[n])
        out[f * 36:(f + 1) * 36] = frame
    return out


def decode_channel(frames):
    n_frames = len(frames) // 36
    out = np.empty(n_frames * SAMPLES_PER_FRAME, dtype=np.int32)
    for f in range(n_frames):
        fr = frames[f * 36:(f + 1) * 36]
        h2, h1, idx = struct.unpack("<hhB", fr[:5])
        pos = f * SAMPLES_PER_FRAME
        out[pos] = h2
        out[pos + 1] = h1
        for i in range(62):
            b = fr[5 + i // 2]
            nib = (b >> 4) & 0xF if i & 1 else b & 0xF
            s = max(-32768, min(32767, int(STEP_TAB[idx][nib]) + 2 * h1 - h2))
            idx = int(NEXT_TAB[idx][nib])
            out[pos + 2 + i] = s
            h2, h1 = h1, s
    return out


def build_wem(channels, payload, num_samples, loop, rate=RATE):
    block = 36 * channels
    fmt = struct.pack("<4sIHHIIHHH6s", b"fmt ", 24, 0x8311, channels, rate,
                      rate * block // SAMPLES_PER_FRAME,
                      block, 4, 6, b"\x00\x00\x02\x31\x00\x00")
    chunks = [fmt, struct.pack("<4sI4s", b"JUNK", 4, b"\x00" * 4)]
    if loop:
        smpl = struct.pack("<4sI", b"smpl", 0x3C)
        smpl += struct.pack("<9I", 0, 0, 0, 60, 0, 0, 0, 1, 0)
        smpl += struct.pack("<6I", 0, 0, 0, 0, num_samples - 1, 0)
        chunks.append(smpl)
    chunks.append(struct.pack("<4sI", b"data", len(payload)) + payload)
    body = b"".join(chunks)
    return b"RIFF" + struct.pack("<I", 4 + len(body)) + b"WAVE" + body


def encode_file(src, out, channels, loop, lookahead=0, heuristic_idx=False, beam=0, rate=RATE, ef=0.0):
    pcm = load_pcm(src, channels, rate)
    num_samples = pcm.shape[0]
    chans = [encode_channel(pcm[:, c], heuristic_idx, lookahead) if beam <= 0
             else encode_channel_beam(pcm[:, c], beam, ef) for c in range(channels)]
    if channels == 1:
        payload = chans[0].tobytes()
    else:
        # 帧级交错: [ch0帧36B][ch1帧36B][ch0帧36B]...
        nf = len(chans[0]) // 36
        frames = np.stack([ch.reshape(nf, 36) for ch in chans], axis=1)
        payload = frames.tobytes()
    wem = build_wem(channels, payload, num_samples, loop, rate)
    with open(out, "wb") as f:
        f.write(wem)
    print(f"{out}: {len(wem):,} B, {rate} Hz x {channels} ch PTADPCM, "
          f"{num_samples / rate:.1f}s" + (" (loop)" if loop else ""))
    return pcm


def decode_wem(path, out_wav):
    with open(path, "rb") as f:
        d = f.read()
    assert d[:4] == b"RIFF" and d[8:12] == b"WAVE"
    off = 12
    fmt = None
    data = None
    while off + 8 <= len(d):
        cid, sz = d[off:off + 4], struct.unpack("<I", d[off + 4:off + 8])[0]
        if cid == b"fmt ":
            fmt = d[off + 8:off + 8 + sz]
        if cid == b"data":
            data = d[off + 8:off + 8 + sz]
            break
        off += 8 + sz
    tag, ch, rate, _avg, block, _bits = struct.unpack("<HHIIHH", fmt[:16])
    assert tag == 0x8311, f"not PTADPCM (tag={hex(tag)})"
    frame_size = block // ch
    assert frame_size == 36, "frame_size != 36"
    n_frames = len(data) // block
    chans = []
    for c in range(ch):
        frames = bytearray(n_frames * 36)
        for f_ in range(n_frames):
            base = f_ * block + c * 36
            frames[f_ * 36:(f_ + 1) * 36] = data[base:base + 36]
        chans.append(decode_channel(bytes(frames)))
    pcm = np.stack(chans, axis=1).astype(np.int16)
    r = subprocess.run([FFMPEG, "-v", "error", "-y", "-f", "s16le", "-ar", str(rate),
                        "-ac", str(ch), "-i", "-", out_wav],
                       input=pcm.tobytes())
    if r.returncode != 0:
        sys.exit("ffmpeg wav write failed")
    print(f"decoded {path} -> {out_wav} ({n_frames * 64 / rate:.1f}s)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["encode", "selftest", "decode"])
    ap.add_argument("src")
    ap.add_argument("out", nargs="?")
    ap.add_argument("--channels", type=int, default=2)
    ap.add_argument("--loop", action="store_true")
    ap.add_argument("--lookahead", type=int, default=0, help="前瞻层数(0/1)，越大越慢越准")
    ap.add_argument("--heuristic", action="store_true", help="开启帧首index启发式(默认关闭)")
    ap.add_argument("--beam", type=int, default=0, help="beam search 束宽(如64)，开启后忽略前瞻/启发式")
    ap.add_argument("--seconds", type=float, default=0, help="selftest 只用前N秒")
    ap.add_argument("--rate", type=int, default=RATE, help="采样率(默认48000)")
    ap.add_argument("--ef", type=float, default=0.0, help="误差反馈系数(0-1, 建议0.7)")
    args = ap.parse_args()

    if args.cmd == "decode":
        if not args.out:
            sys.exit("decode needs output wav path")
        decode_wem(args.src, args.out)
    elif args.cmd == "encode":
        if not args.out:
            sys.exit("encode needs output path")
        encode_file(args.src, args.out, args.channels, args.loop,
                    args.lookahead, args.heuristic, args.beam, args.rate, args.ef)
    else:
        pcm = load_pcm(args.src, args.channels, args.rate)
        if args.seconds > 0:
            pcm = pcm[:int(args.seconds * args.rate)]
        for c in range(args.channels):
            enc = (encode_channel(pcm[:, c], args.heuristic, args.lookahead) if args.beam <= 0
                   else encode_channel_beam(pcm[:, c], args.beam))
            dec = decode_channel(enc.tobytes())[:pcm.shape[0]]
            sig = pcm[:, c].astype(np.float64)
            noise = dec - sig
            snr = 10 * np.log10((sig ** 2).mean() / max((noise ** 2).mean(), 1e-9))
            print(f"ch{c}: SNR {snr:.1f} dB, max err {np.abs(noise).max():.0f}")


if __name__ == "__main__":
    main()
