# 千星奇域 BGM 替换开关工具（路线A：离线资产替换，仅单人图自用/小号）
#
# 子命令:
#   list     列出 pck 内全部 wem 条目
#   extract  抽取一个条目为 .wem 文件
#   patch    用自定义 .wem 原位替换一个条目（新文件必须 <= 原条目大小）
#   restore  用补丁时生成的 .bgmbackup 恢复原始字节
#   status   检查 pck 当前是否处于 patched 状态
#
# 原理: AKPK pck 头部含流式媒体 LUT {u32 fileID, u32 lang, u32 size, u64 offset}。
# patch 在原 offset 写入新 wem 字节并把 LUT size 改为新大小，尾部清零；
# 被覆盖的原始字节（wem 数据 + LUT 记录）先存入 <pck>.bgmbackup 供 restore 使用。
import argparse
import json
import os
import struct
import sys

LUT_ENTRY_SIZE = 20


def read_header(f):
    f.seek(0)
    magic, hsize, ver = struct.unpack("<4sII", f.read(12))
    if magic != b"AKPK":
        raise ValueError("not an AKPK pck")
    lang_sz, sb_sz, stm_sz, ext_sz = struct.unpack("<4I", f.read(16))
    lang_lut_off = f.tell()
    f.seek(lang_sz, 1)
    sb_off = f.tell()
    f.seek(sb_sz, 1)
    stm_off = f.tell()
    f.seek(stm_sz, 1)
    return {
        "header_end": 28 + lang_sz + sb_sz + stm_sz + ext_sz,
        "lang_lut_off": lang_lut_off, "lang_sz": lang_sz,
        "stm_off": stm_off, "stm_sz": stm_sz,
    }


def list_entries(path):
    with open(path, "rb") as f:
        h = read_header(f)
        f.seek(h["stm_off"])
        (count,) = struct.unpack("<I", f.read(4))
        entries = []
        for i in range(count):
            raw = f.read(LUT_ENTRY_SIZE)
            fid, langid, size, off = struct.unpack("<IIIQ", raw)
            entries.append({"fileID": fid, "lang": langid, "size": size,
                            "offset": off, "lut_pos": h["stm_off"] + 4 + i * LUT_ENTRY_SIZE})
        return entries


def backup_path(pck):
    return pck + ".bgmbackup"


def cmd_list(args):
    for e in list_entries(args.pck):
        print(f"{e['fileID']:>12}  lang={e['lang']}  {e['size']:>11,} B  @{e['offset']:,}")


def cmd_extract(args):
    entries = {e["fileID"]: e for e in list_entries(args.pck)}
    e = entries.get(args.file_id)
    if not e:
        sys.exit(f"fileID {args.file_id} not found")
    with open(args.pck, "rb") as f:
        f.seek(e["offset"])
        data = f.read(e["size"])
    with open(args.out, "wb") as f:
        f.write(data)
    print(f"extracted {e['size']:,} B -> {args.out}")


def cmd_patch(args):
    with open(args.wem, "rb") as f:
        new = f.read()
    if new[:4] != b"RIFF" or new[8:12] != b"WAVE":
        sys.exit("replacement is not a RIFF/WAVE (.wem) file")
    entries = {e["fileID"]: e for e in list_entries(args.pck)}
    e = entries.get(args.file_id)
    if not e:
        sys.exit(f"fileID {args.file_id} not found")
    if len(new) > e["size"]:
        sys.exit(f"replacement {len(new):,} B > original {e['size']:,} B; "
                 f"pick a smaller encode or a longer original track")
    bp = backup_path(args.pck)
    if os.path.exists(bp):
        with open(bp, encoding="utf-8") as f:
            backup = json.load(f)
        if any(b["fileID"] == args.file_id for b in backup["entries"]):
            sys.exit(f"fileID {args.file_id} already patched; restore first")
    else:
        backup = {"pck": os.path.abspath(args.pck), "entries": []}
    with open(args.pck, "r+b") as f:
        f.seek(e["offset"])
        orig_wem = f.read(e["size"])
        f.seek(e["lut_pos"])
        orig_lut = f.read(LUT_ENTRY_SIZE)

        # 先持久化音频备份并原子提交清单，再触碰 pck：任一步中断都可安全重试恢复
        bin_name = f"{bp}.{args.file_id}.bin"
        backup["entries"].append({
            "fileID": e["fileID"], "offset": e["offset"], "size": e["size"],
            "lut_pos": e["lut_pos"],
            "orig_wem_b64": None, "orig_wem_file": os.path.basename(bin_name),
            "orig_lut_hex": orig_lut.hex(),
        })
        with open(bin_name, "xb") as bf:
            bf.write(orig_wem)
        try:
            tmp = bp + ".tmp"
            with open(tmp, "w", encoding="utf-8") as mf:
                json.dump(backup, mf, indent=1)
            os.replace(tmp, bp)
        except BaseException:
            os.remove(bin_name)
            raise

        f.seek(e["offset"])
        f.write(new)
        f.write(b"\x00" * (e["size"] - len(new)))
        f.seek(e["lut_pos"])
        f.write(struct.pack("<IIIQ", e["fileID"], e["lang"], len(new), e["offset"]))
        f.flush()
        os.fsync(f.fileno())
    print(f"patched fileID {args.file_id}: {e['size']:,} B -> {len(new):,} B "
          f"(backup: {bp})")


def cmd_restore(args):
    bp = backup_path(args.pck)
    if not os.path.exists(bp):
        sys.exit("no backup; nothing to restore")
    with open(bp, encoding="utf-8") as f:
        backup = json.load(f)
    total = len(backup["entries"])
    with open(args.pck, "r+b") as f:
        for b in list(backup["entries"]):
            bin_name = os.path.join(os.path.dirname(bp), b["orig_wem_file"])
            with open(bin_name, "rb") as bf:
                orig = bf.read()
            lut = bytes.fromhex(b["orig_lut_hex"])
            if len(orig) != b["size"] or len(lut) != LUT_ENTRY_SIZE:
                sys.exit("备份不完整，无法恢复")
            f.seek(b["offset"])
            f.write(orig)
            f.seek(b["lut_pos"])
            f.write(lut)
            f.flush()
            os.fsync(f.fileno())
            f.seek(b["offset"])
            if f.read(len(orig)) != orig:
                sys.exit("恢复校验失败，请重试")
            f.seek(b["lut_pos"])
            if f.read(LUT_ENTRY_SIZE) != lut:
                sys.exit("恢复校验失败，请重试")
            # 清单更新成功后才删除数据，恢复中断时不会留下指向缺失数据的清单
            backup["entries"].remove(b)
            if backup["entries"]:
                tmp = bp + ".tmp"
                with open(tmp, "w", encoding="utf-8") as mf:
                    json.dump(backup, mf, indent=1)
                os.replace(tmp, bp)
            else:
                os.remove(bp)
            os.remove(bin_name)
    print(f"restored {total} entries in {args.pck}")


def cmd_status(args):
    bp = backup_path(args.pck)
    if not os.path.exists(bp):
        print("clean (no backup present)")
        return
    with open(bp, encoding="utf-8") as f:
        backup = json.load(f)
    print(f"PATCHED: {len(backup['entries'])} entries")
    for b in backup["entries"]:
        print(f"  fileID {b['fileID']} @{b['offset']:,}")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name, fn, helptext in [
        ("list", cmd_list, "list wem entries"),
        ("extract", cmd_extract, "extract one entry to .wem"),
        ("patch", cmd_patch, "replace one entry with custom .wem"),
        ("restore", cmd_restore, "restore original bytes from backup"),
        ("status", cmd_status, "show patch status"),
    ]:
        p = sub.add_parser(name, help=helptext)
        p.add_argument("pck")
        if name in ("extract", "patch"):
            p.add_argument("file_id", type=int)
        if name == "extract":
            p.add_argument("out")
        if name == "patch":
            p.add_argument("wem")
        p.set_defaults(fn=fn)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
