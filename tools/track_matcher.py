# 曲名映射构建器：站点曲库(data.json, 含网易云song_id) × 本库pck条目
# 1) 时长整秒唯一匹配直接落定; 2) 冲突桶内用音频指纹(对数梅尔包络互相关)匹配
# 输出: build/bgm-inject-research/track-map.json
import concurrent.futures as cf
import json
import os
import struct
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "vendor", "python-deps"))
import numpy as np  # noqa: E402
sys.path.insert(0, os.path.dirname(__file__))
from ptadpcm_enc import decode_channel  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..")
BUILD = os.path.join(ROOT, "build", "bgm-inject-research")
CACHE = os.path.join(BUILD, "fp_cache")
FFMPEG = os.environ.get("FFMPEG", r"D:\ffmpeg\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe")
SITE_JSON = os.path.join(BUILD, "xiaomol-bgm-data.json")
SITE_URL = "https://oss.xiaomol444.xyz/ugc-tool-data/BgmPlayer/data.json"
NE_URL = "https://music.163.com/song/media/outer/url?id={}.mp3"

GAME_PCK_DIR = None  # 运行时从已解析 json 里的 pck 路径取


def load_site():
    if not os.path.exists(SITE_JSON):
        req = urllib.request.Request(SITE_URL, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=60) as r, open(SITE_JSON, "wb") as f:
            f.write(r.read())
    return json.load(open(SITE_JSON, encoding="utf-8"))["musicData"]


def load_ours():
    ours = []
    import glob
    for jf in sorted(glob.glob(os.path.join(BUILD, "beydmusic*.json"))):
        d = json.load(open(jf))
        for e in d["entries"]:
            ours.append({"fileID": e["fileID"], "pck": d["pck"],
                         "offset": e["offset"], "size": e["size"],
                         "sec": e["size"] // 54000})
    return ours


def decode_ours_pcm(entry):
    """抽取条目 -> PTADPCM 解码 -> 48k 立体声 int16 numpy (N,2)。
    若该条目当前处于 patched 状态，优先读 .bgmbackup 备份里的原始字节。"""
    cache = os.path.join(CACHE, "ours", f"{entry['fileID']}.npy")
    os.makedirs(os.path.dirname(cache), exist_ok=True)
    if os.path.exists(cache):
        return np.load(cache)
    backup_bin = f"{entry['pck']}.bgmbackup.{entry['fileID']}.bin"
    if os.path.exists(backup_bin):
        with open(backup_bin, "rb") as f:
            d = f.read()
    else:
        with open(entry["pck"], "rb") as f:
            f.seek(entry["offset"])
            d = f.read(entry["size"])
    off = 12
    fmt = data = None
    while off + 8 <= len(d):
        cid, sz = d[off:off + 4], struct.unpack("<I", d[off + 4:off + 8])[0]
        if cid == b"fmt ":
            fmt = d[off + 8:off + 8 + sz]
        if cid == b"data":
            data = d[off + 8:off + 8 + sz]
            break
        off += 8 + sz
    _tag, ch, _rate, _avg, block, _bits = struct.unpack("<HHIIHH", fmt[:16])
    n_frames = len(data) // block
    chans = []
    for c in range(ch):
        frames = bytearray(n_frames * 36)
        for f_ in range(n_frames):
            base = f_ * block + c * 36
            frames[f_ * 36:(f_ + 1) * 36] = data[base:base + 36]
        chans.append(decode_channel(bytes(frames)))
    pcm = np.stack(chans, axis=1).astype(np.int16)
    np.save(cache, pcm)
    return pcm


def fetch_site_pcm(track):
    """网易云外链 -> mp3 -> 48k 立体声 int16 numpy"""
    cache = os.path.join(CACHE, "site", f"{track['id']}.npy")
    os.makedirs(os.path.dirname(cache), exist_ok=True)
    if os.path.exists(cache):
        return np.load(cache)
    mp3 = os.path.join(CACHE, "site", f"{track['id']}.mp3")
    if not os.path.exists(mp3):
        req = urllib.request.Request(NE_URL.format(track["song_id"]),
                                     headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=60) as r, open(mp3, "wb") as f:
            f.write(r.read())
        time.sleep(0.3)
    r = subprocess.run([FFMPEG, "-v", "error", "-i", mp3, "-ar", "48000",
                        "-ac", "2", "-f", "s16le", "-"], capture_output=True)
    if r.returncode != 0 or not r.stdout:
        raise RuntimeError(f"ffmpeg decode fail for site id {track['id']}")
    pcm = np.frombuffer(r.stdout, dtype=np.int16).reshape(-1, 2).copy()
    np.save(cache, pcm)
    return pcm


def features(pcm):
    """对数梅尔能量包络 (帧, 带), 供互相关。降采样到 8k 加速。"""
    x = pcm.astype(np.float32).mean(axis=1)
    x = x[::6]  # 48k -> 8k
    n_fft, hop, n_band = 512, 256, 24
    n_fr = max(1, (len(x) - n_fft) // hop + 1)
    win = np.hanning(n_fft).astype(np.float32)
    fr = np.stack([np.abs(np.fft.rfft(x[i * hop:i * hop + n_fft] * win))
                   for i in range(n_fr)])
    freqs = np.fft.rfftfreq(n_fft, 1 / 8000)
    nyq = freqs[-1]
    edges = np.linspace(0, nyq, n_band + 1)
    bands = np.stack([fr[:, (freqs >= edges[i]) & (freqs < edges[i + 1])].mean(axis=1)
                      for i in range(n_band)], axis=1)
    bands = np.nan_to_num(bands)
    feat = np.log1p(bands * 1000)
    feat -= feat.mean(axis=0, keepdims=True)
    std = feat.std(axis=0, keepdims=True) + 1e-6
    return feat / std


def similarity(a, b, max_lag_s=3.0):
    """两个特征序列的最佳滞后余弦相似度(帧均值)。返回 0..1。"""
    hop_s = 256 / 8000
    max_lag = int(max_lag_s / hop_s)
    best = -1.0
    for lag in range(-max_lag, max_lag + 1, 2):
        if lag >= 0:
            aa, bb = a[lag:], b[:len(b) - lag] if lag else b
        else:
            aa, bb = a[:len(a) + lag], b[-lag:]
        L = min(len(aa), len(bb))
        if L < 200:
            continue
        aa, bb = aa[:L], bb[:L]
        s = float((aa * bb).mean(axis=1).mean())
        best = max(best, s)
    return best


def main():
    site = load_site()
    ours = load_ours()
    print(f"site {len(site)} tracks, ours {len(ours)} entries")

    import collections
    site_by_sec = collections.defaultdict(list)
    for t in site:
        site_by_sec[t["time"]].append(t)
    ours_by_sec = collections.defaultdict(list)
    for o in ours:
        ours_by_sec[o["sec"]].append(o)

    result = {}
    buckets = []
    for sec, olist in ours_by_sec.items():
        slist = site_by_sec.get(sec, [])
        if len(olist) == 1 and len(slist) == 1:
            result[olist[0]["fileID"]] = {"id": slist[0]["id"], "name": slist[0]["name"],
                                          "match": "duration", "score": None}
        else:
            buckets.append((sec, olist, slist))
    print(f"duration-unique: {len(result)}, collision buckets: {len(buckets)}")

    for sec, olist, slist in sorted(buckets):
        print(f"bucket {sec}s: {len(olist)} ours x {len(slist)} site", flush=True)
        o_feat = {}
        for o in olist:
            o_feat[o["fileID"]] = features(decode_ours_pcm(o))
        s_feat = {}
        with cf.ThreadPoolExecutor(4) as ex:
            futs = {ex.submit(fetch_site_pcm, t): t for t in slist}
            for fu in cf.as_completed(futs):
                t = futs[fu]
                try:
                    s_feat[t["id"]] = features(fu.result())
                except Exception as e:
                    print(f"  site {t['id']} fetch/decode failed: {e}")
        pairs = []
        for o in olist:
            for t in slist:
                if t["id"] not in s_feat:
                    continue
                pairs.append((similarity(o_feat[o["fileID"]], s_feat[t["id"]]),
                              o["fileID"], t["id"], t["name"]))
        pairs.sort(reverse=True)
        used_o, used_t = set(), set()
        for score, fid, tid, name in pairs:
            if fid in used_o or tid in used_t:
                continue
            used_o.add(fid)
            used_t.add(tid)
            result[fid] = {"id": tid, "name": name, "match": "fingerprint",
                           "score": round(score, 4)}
            print(f"  {fid} <- {tid} {name}  score={score:.3f}", flush=True)

    tracks = []
    for o in ours:
        m = result.get(o["fileID"])
        if m:
            tracks.append({"index": m["id"], "name": m["name"], "fileID": o["fileID"],
                           "pck": os.path.basename(o["pck"]), "durationSec": int(o["sec"]),
                           "match": m["match"], "score": m["score"]})
    tracks.sort(key=lambda t: t["index"])
    out = os.path.join(BUILD, "track-map.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"tracks": tracks}, f, ensure_ascii=False, indent=1)
    n_fp = sum(1 for t in tracks if t["match"] == "fingerprint")
    low = [t for t in tracks if t["match"] == "fingerprint" and (t["score"] or 0) < 0.5]
    print(f"total mapped {len(tracks)}/188 (fingerprint {n_fp}, low-confidence {len(low)})")
    print(f"-> {out}")


if __name__ == "__main__":
    main()
