#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""千星奇域 BGM/音效离线播放器

解析千星地图存储(.gil 节点图 + external_lua_file 脚本)中的音频播放指令,
在游戏外按引擎语义渲染/播放:

- BGM cue(设置玩家背景音乐节点): 曲目id + 源片段[start,end] + 变速speed(音高∝速度,连续比率)
  + 音量volume(线性幅度 v/100)。cue 数据来自图变量(HB_* 表)或构建产物(lab-cues.json)。
- 特效器原生页(音效时间轴资产): score 记录 {start_ms,duration_ms,audio_id,volume,speed},
  按页 on_ms 偏移叠加。
- Lua 调度计划(NATIVE_CASES.plan): 逐音符 {on_ms,duration_ms,cue_arg,action} 时间轴。

音源: ①游戏原 wem(BeydMusic,自研 PTADPCM 解码,精确) ②QxMusicFitter 已抽 mp3(回退,快)。
用法:
  python tools/qx_player.py list <地图目录|gil文件> [--cues lab-cues.json]
  python tools/qx_player.py render <地图目录|gil文件> --case 207 [--cues ...] [--assets <score目录>]
      [--out out.wav] [--mp3] [--play] [--stems lead,accomp]
"""
import argparse
import json
import os
import re
import subprocess
import sys
import wave
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
import sys as _sys
_sys.path.insert(0, str(REPO_ROOT / "vendor" / "python-deps"))
SR = 44100
DEFAULT_GT = r"D:/ProgramSource2/th08-qianxing/outputs/th08-bgm/node_modules/genshin-ts"
SFX_MP3_DIR = Path(r"D:/ProgramSource2/QxMusicFitter/assets/sfx")
BGM_MP3_DIR = Path(r"D:/ProgramSource2/QxMusicFitter/assets/bgm")
FFMPEG = r"D:/ffmpeg/ffmpeg-9.0.1-full_build/bin/ffmpeg.exe"
WEM_CACHE = REPO_ROOT / "build" / "player-cache"

# ---------------------------------------------------------------- GIL 解析(经 node 提取器)


def extract_gil(gil_path: Path, out_json: Path) -> dict:
    out_json.parent.mkdir(parents=True, exist_ok=True)
    script = REPO_ROOT / "tools" / "qx_extract.mjs"
    cmd = ["node", str(script), str(gil_path), str(out_json), "--genshin-ts", DEFAULT_GT]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", shell=(os.name == "nt"))
    if r.returncode != 0:
        raise RuntimeError("GIL 提取失败: " + (r.stderr or r.stdout)[-2000:])
    print(r.stdout.strip())
    return json.loads(out_json.read_text(encoding="utf-8"))


# ---------------------------------------------------------------- Lua 静态解析

_NUM = r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"


def parse_lua_cases(lua_text: str) -> list:
    """解析 NATIVE_CASES 表: 每案 {arg,mode,name,duration,plan[],native_pages[],counts}"""
    cases = []
    for m in re.finditer(r'\{arg=(\d+),mode="(\w+)",id=(-?\d+),name="([^"]*)"', lua_text):
        i = m.start()
        # 截到本案结尾(下一个 {arg= 或文件尾)
        nxt = lua_text.find(',mode="', lua_text.find("{arg=", m.end()))
        nxt = lua_text.rfind("{arg=", 0, nxt) if nxt > 0 else -1
        seg = lua_text[i:nxt if nxt > 0 else len(lua_text)]
        case = {
            "arg": int(m.group(1)), "mode": m.group(2), "id": int(m.group(3)),
            "name": m.group(4), "plan": [], "native_pages": [],
        }
        md = re.search(r'description="([^"]*)"', seg)
        if md:
            case["description"] = md.group(1)
        md = re.search(r"duration=(" + _NUM + ")", seg)
        if md:
            case["duration"] = float(md.group(1))
        for mm in re.finditer(
                r'\{on_ms=(' + _NUM + r'),duration_ms=(' + _NUM + r'),cue_arg=(\d+),action="(\w+)"', seg):
            case["plan"].append({
                "on_ms": float(mm.group(1)), "duration_ms": float(mm.group(2)),
                "cue_arg": int(mm.group(3)), "action": mm.group(4)})
        for mm in re.finditer(
                r'\{arg=(\d+),config_id=(\d+),on_ms=(' + _NUM + r'),lead_in_ms=(' + _NUM +
                r'),container_ms=(' + _NUM + r'),events=(\d+)\}', seg):
            case["native_pages"].append({
                "arg": int(mm.group(1)), "config_id": int(mm.group(2)),
                "on_ms": float(mm.group(3)), "lead_in_ms": float(mm.group(4)),
                "container_ms": float(mm.group(5)), "events": int(mm.group(6))})
        md = re.search(r"bgm_cue_count=(\d+),rest_count=(\d+),cue_table_count=(\d+)", seg)
        if md:
            case["bgm_cue_count"] = int(md.group(1))
            case["cue_table_count"] = int(md.group(3))
        cases.append(case)
    return cases


def scan_lua_playaudio(lua_text: str) -> list:
    """通用: 扫描 game.PlayAudio2D(数字) 直接调用(客户端路径)"""
    return [int(x) for x in re.findall(r"PlayAudio2D\s*\(\s*(\d+)\s*\)", lua_text)]


# ---------------------------------------------------------------- 音源

_track_map = None


def track_map() -> dict:
    global _track_map
    if _track_map is None:
        p = Path(r"D:/ProgramSource2/qxbgm-registry/config/track-map.json")
        _track_map = {t["index"]: t for t in json.loads(p.read_text(encoding="utf-8"))["tracks"]}
    return _track_map


def load_track_wav(bgm_id: int, use_mp3: bool) -> tuple:
    """返回 (samples@SR, mono float32, 来源描述)。优先精确原 wem,回退 mp3。"""
    WEM_CACHE.mkdir(parents=True, exist_ok=True)
    if not use_mp3:
        t = track_map().get(bgm_id)
        if t:
            wav = WEM_CACHE / f"{bgm_id}.wav"
            if not wav.exists():
                wem = WEM_CACHE / f"{bgm_id}.wem"
                if not wem.exists():
                    r = subprocess.run(
                        [sys.executable, str(REPO_ROOT / "tools" / "bgm_swap.py"), "extract",
                         t["pck"] if Path(t["pck"]).is_absolute() else str(
                             Path(r"F:/Program Files/Genshin Impact/Genshin Impact Game/YuanShen_Data/StreamingAssets/AudioAssets/BeyondUGC") / t["pck"]),
                         str(t["fileID"]), str(wem)],
                        capture_output=True, text=True, encoding="utf-8")
                    if r.returncode != 0:
                        raise RuntimeError("wem 抽取失败: " + (r.stderr or "")[-500:])
                r = subprocess.run(
                    [sys.executable, str(REPO_ROOT / "tools" / "ptadpcm_enc.py"), "decode", str(wem), str(wav)],
                    capture_output=True, text=True, encoding="utf-8")
                if r.returncode != 0:
                    raise RuntimeError("PTADPCM 解码失败: " + (r.stderr or "")[-500:])
            import numpy as np
            with wave.open(str(wav), "rb") as w:
                data = w.readframes(w.getnframes())
                nch = w.getnchannels()
                sw = w.getsampwidth()
                sr = w.getframerate()
            arr = np.frombuffer(data, dtype=np.int16 if sw == 2 else np.uint8).astype(np.float32) / 32768.0
            if nch > 1:
                arr = arr.reshape(-1, nch).mean(axis=1)
            if sr != SR:
                # 简单线性重采样到统一采样率(变调不产生:仅播放器内部格式统一)
                import numpy as np
                idx = np.arange(0, len(arr) - 1, sr / SR)
                arr = np.interp(idx, np.arange(len(arr)), arr).astype(np.float32)
            return arr, f"wem#{t['fileID']}({t['pck']})"
    mp3 = BGM_MP3_DIR / f"{bgm_id}.mp3"
    if mp3.exists():
        return _decode_mp3_mono(mp3), f"mp3:{mp3.name}"
    raise RuntimeError(f"找不到曲目 {bgm_id} 的音源(无 track-map 条目且无 mp3)")


def _decode_mp3_mono(path: Path):
    import numpy as np
    r = subprocess.run([FFMPEG, "-v", "error", "-i", str(path), "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                       capture_output=True)
    if r.returncode != 0:
        raise RuntimeError("ffmpeg 解码失败: " + r.stderr[-300:].decode(errors="replace"))
    return np.frombuffer(r.stdout, dtype=np.float32).copy()


def load_sfx(audio_id: int):
    mp3 = SFX_MP3_DIR / f"{audio_id}.mp3"
    if not mp3.exists():
        return None
    return _decode_mp3_mono(mp3)


# ---------------------------------------------------------------- 渲染

def render_cue(track_cache, cue, use_mp3):
    """一个 BGM cue → 音频片段。语义: 片段[start,end]按 speed 变速播放(音高∝speed),
    音量 = volume/100 线性幅度;自然时长 = (end-start)/speed。"""
    import numpy as np
    bgm_id = int(cue["bgm_id"])
    if bgm_id not in track_cache:
        track_cache[bgm_id] = load_track_wav(bgm_id, use_mp3)
    src, srctext = track_cache[bgm_id]
    start, end, speed, vol = float(cue["source_start"]), float(cue["source_end"]), float(cue["speed"]), float(cue["volume"])
    i0, i1 = int(start * SR), min(int(end * SR), len(src))
    seg = src[i0:i1]
    if len(seg) < 8 or speed <= 0:
        return None, 0.0
    # varispeed: 读指针步进 = 1/speed → 时长/速度、音高×速度
    n_out = int(len(seg) / speed)
    idx = np.arange(n_out) * speed
    out = np.interp(idx, np.arange(len(seg)), seg).astype(np.float32) * (vol / 100.0)
    return out, n_out / SR


def load_score_records(assets_dir) -> dict:
    """score 目录(构建产物 *-score.json,可逗号分隔多个)→ {config_id: notes[]}"""
    scores = {}
    if not assets_dir:
        return scores
    dirs = [Path(x) for x in str(assets_dir).split(",") if x.strip()]
    for p in []:
        pass
    files = []
    for d in dirs:
        if d.exists():
            files.extend(d.glob("*-score.json"))
    for p in files:
        cid = int(p.name.split("-")[0])
        d = json.loads(p.read_text(encoding="utf-8"))
        scores[cid] = d.get("notes", [])
    return scores


def render(args):
    import numpy as np
    if getattr(args, "case_json", None) and not Path(args.target).exists():
        # 纯 case-JSON 模式(常配 --trace 验证):不需要地图
        cues = {}
        if args.cues:
            raw = json.loads(Path(args.cues).read_text(encoding="utf-8"))
            for row in raw:
                cues[row["arg"]] = row
        sel = json.loads(Path(args.case_json).read_text(encoding="utf-8"))
        _render_case(sel, cues, args, lua_text="", direct_ids=[])
        return
    target = Path(args.target)
    if target.is_dir():
        gils = sorted(target.glob("*.gil"))
        if not gils:
            raise RuntimeError("目录下没有 .gil")
        gil = gils[0]
        lua_dir = target / "external_lua_file"
    else:
        gil = target
        lua_dir = gil.parent / "external_lua_file"

    work = WEM_CACHE / "extract"
    work.mkdir(parents=True, exist_ok=True)
    out_json = work / (gil.stem + ".json")
    out_json.parent.mkdir(parents=True, exist_ok=True)
    gil_data = extract_gil(gil, out_json)

    # cue 表: 优先外部全量表(--cues,arg 键);缺省从图内分页常量数组重建,再回退图变量 HB_*
    cues = {}
    if args.cues:
        raw = json.loads(Path(args.cues).read_text(encoding="utf-8"))
        for row in raw:
            cues[row["arg"]] = row
    else:
        for g in gil_data["graphs"]:
            # 分页常量数组节点(nodeId 169):生成器按页交错输出 ids,starts,ends,volumes,speeds
            arrays = [n for n in g.get("nodes", []) if n.get("array")]
            pages = {"ids": [], "starts": [], "ends": [], "volumes": [], "speeds": []}
            def looks_like(a, kind):
                if not a:
                    return False
                ints = all(isinstance(x, int) for x in a)
                if kind == "ids":
                    return ints and all(10001 <= x <= 20000 for x in a)
                if kind == "volumes":
                    return ints and all(0 <= x <= 100 for x in a)
                if kind in ("starts", "ends"):
                    return (not ints) and all(0 < x < 600 for x in a)
                if kind == "speeds":
                    return (not ints) and all(0.01 <= x <= 8.0 for x in a)
                return False
            order = ("ids", "starts", "ends", "volumes", "speeds")
            i = 0
            while i < len(arrays):
                if i + 5 <= len(arrays):
                    five = [arrays[i + k]["array"] for k in range(5)]
                    if all(looks_like(five[k], order[k]) for k in range(5)) and                             sum(1 for a, b in zip(five[1], five[2]) if a <= b) >= len(five[1]) * 0.9:
                        for k in range(5):
                            pages[order[k]].append(five[k])
                        i += 5
                        continue
                i += 1
            if len(pages["ids"]) and len(pages["ids"]) == len(pages["starts"]) == len(pages["ends"]) \
                    == len(pages["speeds"]) and (len(pages["volumes"]) in (0, len(pages["ids"]))):
                vols = pages["volumes"] or [[100] * len(p) for p in pages["ids"]]
                for p in range(len(pages["ids"])):
                    for r in range(len(pages["ids"][p])):
                        # 引擎 1 基分页: arg∈[page*100+1, page*100+100], index=arg-1-page*100
                        cues[p * 100 + r + 1] = {
                            "bgm_id": pages["ids"][p][r], "source_start": pages["starts"][p][r],
                            "source_end": pages["ends"][p][r], "volume": vols[p][r] if r < len(vols[p]) else 100,
                            "speed": pages["speeds"][p][r]}
                print(f"cue 表由图内分页常量重建: {len(pages['ids'])} 页 × ~100 行 = {len(cues)} cue")
            else:
                v = g["vars"]
                if "HB_ids" in v and "HB_speeds" in v:
                    for i in range(len(v["HB_ids"])):
                        cues[i] = {
                            "bgm_id": v["HB_ids"][i], "source_start": v["HB_starts"][i],
                            "source_end": v["HB_ends"][i], "volume": v["HB_volumes"][i],
                            "speed": v["HB_speeds"][i]}
                    print(f"cue 表来自图 #{g['id']} \"{g['name']}\" 变量: {len(cues)} 行")

    # Lua case 计划
    lua_text = ""
    if lua_dir.exists():
        for lf in sorted(lua_dir.glob("*.lua")):
            lua_text += lf.read_text(encoding="utf-8", errors="replace")
    cases = parse_lua_cases(lua_text)
    direct_ids = scan_lua_playaudio(lua_text)

    cases = parse_lua_cases(lua_text)
    sel = None
    if args.case:
        sel = next((c for c in cases if c["arg"] == args.case), None)
        if sel is None:
            raise RuntimeError(f"Lua 中没有 case {args.case};可选: " + ", ".join(str(c["arg"]) for c in cases))
    elif cases:
        sel = cases[0]
    if sel:
        _render_case(sel, cues, args, lua_text=lua_text, direct_ids=scan_lua_playaudio(lua_text))
    else:
        raise RuntimeError("没有可渲染的 case")


def _render_case(sel, cues, args, lua_text="", direct_ids=None):
    import numpy as np
    direct_ids = direct_ids or []

    mode = getattr(args, "mode", "runtime") or "runtime"
    fps = float(getattr(args, "fps", 60) or 60)
    lo, hi = (float(x) for x in str(getattr(args, "speed_window", "0.5,2.0")).split(","))
    print(f"渲染 case {sel['arg']} 「{sel['name']}」: plan {len(sel['plan'])} 项, 原生页 {len(sel['native_pages'])} 个"
          f" | 模式={mode} fps={fps} 速度窗=[{lo},{hi}]")

    dispatches, summary = simulate_runtime(sel, mode=mode, fps=fps)
    print("调度摘要: " + json.dumps(summary, ensure_ascii=False))

    total_ms = (sel.get("duration") or 30) * 1000 + 2000
    buf = np.zeros(int(total_ms / 1000 * SR) + SR, dtype=np.float32)
    track_cache = {}
    stems = (args.stems or "lead,accomp").split(",")
    audit = []

    # ---- 主旋律:单路 BGM,按派发顺序渲染;每个新派发(play/pause)截断前一个 ----
    cue_disp = [d for d in dispatches if d["kind"] == "cue" and d["status"] == "sent"]
    if "lead" in stems:
        active = None  # (audit_index, dispatch, seg)
        for d in cue_disp:
            ev = d["ev"]
            if active is not None:
                ai, a_d, seg = active
                cut_ms = d["dispatch_ms"]
                n = max(0, int((cut_ms - a_d["dispatch_ms"]) / 1000 * SR))
                if n < len(seg):
                    seg = seg[:n]
                    audit[ai]["killed_by_next_ms"] = round(cut_ms, 1)
                place(buf, seg, a_d["dispatch_ms"])
                audit[ai]["final_s"] = round(len(seg) / SR, 3)
                active = None
            if ev["action"] == "pause":
                continue
            cue = cues.get(ev["cue_arg"])
            if cue is None:
                audit.append({**ev, "dispatch_ms": d["dispatch_ms"], "status": "无cue数据"})
                continue
            spd = float(cue["speed"])
            entry = {"cue_arg": ev["cue_arg"], "on_ms": ev["on_ms"], "dispatch_ms": round(d["dispatch_ms"], 1),
                     "late_ms": round(d["late_ms"], 1), "bgm_id": cue["bgm_id"],
                     "speed": round(spd, 4), "vol": cue["volume"]}
            if mode == "runtime" and (spd < lo - 1e-9 or spd > hi + 1e-9):
                # 实测(v26探针 reports/coordination/20261022-bgm-speed-probe-v26):
                # 越界速度服务端接受但客户端完全不发声(不钳制不回退),通道可被后续合法段恢复
                entry["status"] = "engine_silent_out_of_range"
                audit.append(entry)
                continue
            seg, dur = render_cue(track_cache, cue, args.mp3)
            if seg is None:
                entry["status"] = "空片段"
                audit.append(entry)
                continue
            entry["status"] = "rendered"
            audit.append(entry)
            active = (len(audit) - 1, d, seg)
        if active is not None:
            ai, a_d, seg = active
            place(buf, seg, a_d["dispatch_ms"])
            audit[ai]["final_s"] = round(len(seg) / SR, 3)
    # ---- 原生页:页派发时刻+score start_ms;页丢弃则其全部音符不发声 ----
    scores = load_score_records(args.assets)
    if "accomp" in stems and sel["native_pages"]:
        if not scores:
            print(f"!! {len(sel['native_pages'])} 个原生页未渲染(需 --assets 指向 score 目录)")
        done = 0
        for d in dispatches:
            if d["kind"] != "page":
                continue
            pg = d["pg"]
            if d["status"] != "sent":
                audit.append({"page": pg["arg"], "config_id": pg["config_id"], "due_ms": pg["on_ms"],
                              "status": "page_dropped_" + d["status"], "notes_lost": pg.get("events", 0)})
                continue
            notes = scores.get(pg["config_id"])
            if not notes:
                continue
            base = d["dispatch_ms"]
            for nt in notes:
                sfx = load_sfx(int(nt["audio_id"]))
                if sfx is None:
                    continue
                vol = float(nt.get("volume", 100)) / 100.0
                spd = float(nt.get("speed", 1) or 1)
                if abs(spd - 1.0) > 1e-6:
                    n_out = int(len(sfx) / spd)
                    sfx = np.interp(np.arange(n_out) * spd, np.arange(len(sfx)), sfx).astype(np.float32)
                sfx = sfx * vol
                off = int(nt.get("source_offset_ms", 0) / 1000 * SR)
                if off:
                    sfx = sfx[off:]
                dur_n = int(float(nt.get("duration_ms", 1e9)) / 1000 * SR)
                if 0 < dur_n < len(sfx):
                    sfx = sfx[:dur_n]
                if place(buf, sfx, base + float(nt["start_ms"])):
                    done += 1
        if done:
            print(f"原生页音效渲染 {done} 条")
    if direct_ids:
        print(f"注: Lua 检出 {len(direct_ids)} 处 game.PlayAudio2D 直接调用(事件驱动,不参与时间轴渲染): 示例 {direct_ids[:8]}")

    # ---- trace 交叉验证 ----
    if getattr(args, "trace", None):
        validate_trace(dispatches, args.trace)

    peak = float(np.abs(buf).max()) if len(buf) else 0.0
    if peak > 0.99:
        buf *= 0.99 / peak
        print(f"* 混音峰值 {peak:.3f} >1,已整体限幅")
    out = Path(args.out or f"build/player-out/case_{sel['arg']}.wav")
    out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(out), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((buf * 32767).astype(np.int16).tobytes())
    (out.with_suffix(".audit.json")).write_text(
        json.dumps({"case": {k: sel.get(k) for k in ("arg", "name", "duration", "lead_in_ms")},
                    "mode": mode, "fps": fps, "summary": summary, "events": audit},
                   ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"输出: {out} ({len(buf)/SR:.1f}s, 峰值 {peak:.3f}); 审计 {len(audit)} 条 -> {out.with_suffix('.audit.json').name}")
    if args.play:
        subprocess.Popen([FFMPEG.replace('ffmpeg.exe', 'ffplay.exe'), "-nodisp", "-autoexit", str(out)],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def place(buf, seg, at_ms):
    """把片段叠加到缓冲指定毫秒处;越界自动扩容。返回是否放置。"""
    if seg is None or len(seg) == 0 or at_ms < 0:
        return False
    o = int(at_ms / 1000 * SR)
    if o > len(buf) * 4:
        return False
    if o + len(seg) > len(buf):
        buf.resize(o + len(seg), refcheck=False)
    buf[o:o + len(seg)] += seg
    return True


def simulate_runtime(sel, mode="runtime", fps=60.0):
    """按 music_lab.lua 的帧调度语义(逐帧 ≤1 事件)模拟派发时间轴。
    语义来源(music_lab.lua tickBgm/tickNativePages 逐行):
      cue: due=lead+on_ms-ahead;同帧内后到事件"取代"丢弃前事件(superseded_in_frame);
           迟到超过自身时长整条丢弃(expired);否则本帧派发(late 记录)。
      page: on_ms<=elapsed 即消费;迟到>100ms 整页丢弃;同帧仅最后一个候选派发。
    ideal 模式 = 全部按谱面时刻派发,无丢弃(对照用)。"""
    plan = sorted(sel["plan"], key=lambda e: e["on_ms"])
    pages = sorted(sel.get("native_pages", []), key=lambda p: p["on_ms"])
    lead = float(sel.get("lead_in_ms", 1000) or 1000)
    ahead = float(sel.get("ahead_ms", 0) or 0)
    end_ms = float(sel.get("duration") or 0) * 1000
    dispatches = []
    summary = {"sent": 0, "superseded": 0, "expired": 0, "pages_sent": 0, "pages_late_drop": 0, "max_late_ms": 0.0}
    if mode == "ideal":
        for ev in plan:
            dispatches.append({"kind": "cue", "ev": ev, "status": "sent",
                               "dispatch_ms": lead + ev["on_ms"] - ahead, "late_ms": 0.0})
            summary["sent"] += 1
        for pg in pages:
            dispatches.append({"kind": "page", "pg": pg, "status": "sent", "dispatch_ms": pg["on_ms"], "late_ms": 0.0})
            summary["pages_sent"] += 1
        return dispatches, summary
    dt = 1.0 / fps
    t = 0.0
    ci = 0
    pi = 0
    while t * 1000 <= end_ms + 200:
        elapsed = t * 1000
        cand_p = None
        while pi < len(pages):
            pg = pages[pi]
            if pg["on_ms"] > elapsed + 1e-4:
                break
            pi += 1
            late = max(0.0, elapsed - pg["on_ms"])
            if late > 100.0:
                dispatches.append({"kind": "page", "pg": pg, "status": "late_over_100ms",
                                   "dispatch_ms": elapsed, "late_ms": late})
                summary["pages_late_drop"] += 1
            else:
                cand_p = pg
        if cand_p is not None:
            dispatches.append({"kind": "page", "pg": cand_p, "status": "sent", "dispatch_ms": elapsed,
                               "late_ms": max(0.0, elapsed - cand_p["on_ms"])})
            summary["pages_sent"] += 1
        cand = None
        while ci < len(plan):
            ev = plan[ci]
            due = lead + ev["on_ms"] - ahead
            if due > elapsed + 1e-4:
                break
            if cand is not None:
                dispatches.append({"kind": "cue", "ev": cand, "status": "superseded_in_frame",
                                   "dispatch_ms": elapsed, "late_ms": 0.0})
                summary["superseded"] += 1
            cand = ev
            ci += 1
        if cand is not None:
            due = lead + cand["on_ms"] - ahead
            late = max(0.0, elapsed - due)
            summary["max_late_ms"] = max(summary["max_late_ms"], late)
            if elapsed >= lead + cand["on_ms"] + cand["duration_ms"]:
                dispatches.append({"kind": "cue", "ev": cand, "status": "expired",
                                   "dispatch_ms": elapsed, "late_ms": late})
                summary["expired"] += 1
            else:
                dispatches.append({"kind": "cue", "ev": cand, "status": "sent",
                                   "dispatch_ms": elapsed, "late_ms": late})
                summary["sent"] += 1
        t += dt
    return dispatches, summary


def validate_trace(dispatches, trace_path):
    """与 codex 侧帧级实测轨迹(client-trace.json: events=[seq,t_s,op,arg,n])对照。
    比对 op22 的派发序列(arg 顺序)与时刻差。"""
    tr = json.loads(Path(trace_path).read_text(encoding="utf-8"))["events"]
    # op22=音符(play),op25=休止(rest,arg=0);trace 时间含选案偏移,先按 arg 序配对再求对齐偏移
    tr_plays = [(round(t * 1000, 1), a) for _s, t, op, a, _n in tr if op == 22]
    my_plays = [(round(d["dispatch_ms"], 1), d["ev"]["cue_arg"]) for d in dispatches
                if d["kind"] == "cue" and d["status"] == "sent" and d["ev"]["action"] != "pause"]
    tr_rests = [(round(t * 1000, 1)) for _s, t, op, a, _n in tr if op == 25 and a == 0]
    my_rests = [round(d["dispatch_ms"], 1) for d in dispatches
                if d["kind"] == "cue" and d["status"] == "sent" and d["ev"]["action"] == "pause"]
    tr_all = sorted(tr_plays, key=lambda x: x[0])
    my_all = sorted(my_plays, key=lambda x: x[0])
    if tr_all and my_all:
        # 用共同长度的前缀按 arg 序配对求中位偏移(稳健于个别缺发)
        n = min(len(tr_all), len(my_all))
        offs = sorted((ta - tb) for (ta, a), (tb, b) in zip(tr_all[:n], my_all[:n]) if a == b)
        off = offs[len(offs) // 2] if offs else (tr_all[0][0] - my_all[0][0])
        tr_all = [(t - off, a) for t, a in tr_all]
        print(f"[trace] 时间对齐偏移 {off:.1f}ms(按 arg 配对中位数);rest: trace={len(tr_rests)} 模拟={len(my_rests)}")
    if len(tr_all) == len(my_all):
        seq_ok = all(a == b for (_ta, a), (_tb, b) in zip(tr_all, my_all))
        dt_ms = [abs(a[0] - b[0]) for a, b in zip(tr_all, my_all)]
        print(f"[trace] 事件数一致({len(tr_all)});序列{'一致' if seq_ok else '不一致!'};"
              f"时刻差 max={max(dt_ms):.1f}ms mean={sum(dt_ms)/len(dt_ms):.1f}ms")
        if not seq_ok:
            for i, ((ta, a), (tb, b)) in enumerate(zip(tr_all, my_all)):
                if a != b:
                    print(f"  首个分歧 @{i}: trace arg={a}@{ta}ms vs 模拟 arg={b}@{tb}ms")
                    break
    else:
        print(f"[trace] 事件数不一致: trace={len(tr_all)} 模拟={len(my_all)}")
        print("  trace 前6: " + "; ".join(f"{a}@{t:.0f}" for t, a in tr_all[:6]))
        print("  模拟  前6: " + "; ".join(f"{b}@{t:.0f}" for t, b in my_all[:6]))

def list_map(args):
    target = Path(args.target)
    gil = target if target.is_file() else sorted(target.glob("*.gil"))[0]
    lua_dir = gil.parent / "external_lua_file"
    gil_data = extract_gil(gil, WEM_CACHE / "extract" / (gil.stem + ".json"))
    WEM_CACHE.mkdir(parents=True, exist_ok=True)
    print(f"\n== GIL: {gil}")
    for g in gil_data["graphs"]:
        print(f"  图 #{g['id']} \"{g['name']}\" 节点 {g['nodeCount']} 变量 {len(g['vars'])}")
        hb = {k: len(v) for k, v in g["vars"].items() if isinstance(v, list)}
        if hb:
            print(f"    cue 表: {hb}")
    if lua_dir.exists():
        print(f"\n== Lua: {lua_dir}")
        for lf in sorted(lua_dir.glob("*.lua")):
            text = lf.read_text(encoding="utf-8", errors="replace")
            cases = parse_lua_cases(text)
            ids = scan_lua_playaudio(text)
            print(f"  {lf.name} ({len(text)//1024}KB): cases={len(cases)} PlayAudio2D调用={len(ids)}")
            for c in cases:
                cov = c.get("cue_table_count", "?")
                print(f"    case {c['arg']} 「{c['name']}」 {c['mode']} plan={len(c['plan'])} 页={len(c['native_pages'])} cue表={cov}")
    else:
        print("\n(无 external_lua_file 目录)")


def main():
    ap = argparse.ArgumentParser(description="千星奇域 BGM/音效离线播放器")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name in ("list", "render"):
        p = sub.add_parser(name)
        p.add_argument("target", help="地图目录(含 .gil 与 external_lua_file)或 .gil 文件")
        if name == "render":
            p.add_argument("--case", type=int)
            p.add_argument("--cues", help="全量 cue 表 JSON(lab-cues.json, arg 键)")
            p.add_argument("--assets", help="特效器 score 目录(*-score.json)")
            p.add_argument("--out")
            p.add_argument("--mp3", action="store_true", help="BGM 用 mp3 音源(快,非精确)")
            p.add_argument("--mode", choices=["runtime", "ideal"], default="runtime",
                           help="runtime=按 Lua 帧调度+引擎限幅复刻实机行为(默认); ideal=谱面理想渲染")
            p.add_argument("--fps", type=float, default=60.0, help="客户端帧率(默认 60)")
            p.add_argument("--speed-window", default="0.5,2.0",
                           help="BGM 速度发声窗(实测 [0.5,2.0],越界=静音)")
            p.add_argument("--case-json", help="直接读取 case JSON(如 lab-case.json,绕过 Lua 解析)")
            p.add_argument("--trace", help="帧级实测轨迹 client-trace.json 做派发交叉验证")
            p.add_argument("--stems", default="lead,accomp", help="lead,accomp 或仅其一")
            p.add_argument("--play", action="store_true", help="渲染后调用 ffplay 播放")
    args = ap.parse_args()
    (list_map if args.cmd == "list" else render)(args)


if __name__ == "__main__":
    main()
