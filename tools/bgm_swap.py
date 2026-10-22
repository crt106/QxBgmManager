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
import hashlib
import uuid

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
        return read_entries(f)


def read_entries(f):
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


def open_package(path):
    # Windows 与 C# 使用相同共享模式：持有句柄期间允许读取、禁止其他进程写入。
    if os.name != "nt":
        return open(path, "r+b")
    import ctypes
    import msvcrt
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    handle = kernel.CreateFileW(os.path.abspath(path), 0xC0000000, 1, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
    except BaseException:
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle(handle)
        raise
    return os.fdopen(fd, "r+b")


def backup_path(pck):
    return pck + ".bgmbackup"


def data_path(pck, name):
    if not name or os.path.basename(name) != name or "/" in name or "\\" in name:
        raise ValueError("备份文件名无效")
    return os.path.join(os.path.dirname(os.path.abspath(pck)), name)


def atomic_bytes(path, data, overwrite=True):
    if not overwrite and os.path.exists(path):
        raise FileExistsError(path)
    temp = path + "." + uuid.uuid4().hex + ".tmp"
    try:
        with open(temp, "xb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
        if overwrite:
            os.replace(temp, path)
        else:
            os.rename(temp, path)
    finally:
        if os.path.exists(temp):
            os.remove(temp)


def save_backup(path, backup):
    atomic_bytes(path, json.dumps(backup, ensure_ascii=False, indent=1).encode("utf-8"))


def canonical_hash(f, pck, backup):
    ranges = []
    for b in backup["entries"]:
        ranges.append((b["lut_pos"], LUT_ENTRY_SIZE, bytes.fromhex(b["orig_lut_hex"]), None))
        ranges.append((b["offset"], b["size"], None, data_path(pck, b["orig_wem_file"])))
    digest = hashlib.sha256()
    length = os.fstat(f.fileno()).st_size
    def append(source, count):
        while count:
            chunk = source.read(min(count, 65536))
            if not chunk:
                raise ValueError("音乐包或备份不完整")
            digest.update(chunk)
            count -= len(chunk)
    f.seek(0)
    for offset, size, raw, path in sorted(ranges):
        if offset < f.tell() or offset + size > length:
            raise ValueError("备份区段无效")
        append(f, offset - f.tell())
        if path:
            with open(path, "rb") as original:
                append(original, size)
        else:
            digest.update(raw)
        f.seek(offset + size)
    append(f, length - f.tell())
    return digest.hexdigest()


def validate_backup(f, pck, backup):
    length = os.fstat(f.fileno()).st_size
    if backup.get("pck_length") is not None and length != backup["pck_length"]:
        raise ValueError("音乐包已变化，无法使用这份备份")
    h = read_header(f)
    f.seek(h["stm_off"])
    count = struct.unpack("<I", f.read(4))[0]
    entries = {}
    for i in range(count):
        raw = f.read(LUT_ENTRY_SIZE)
        fid, lang, size, offset = struct.unpack("<IIIQ", raw)
        if fid in entries:
            raise ValueError("音乐编号重复")
        entries[fid] = (lang, size, offset, h["stm_off"] + 4 + i * LUT_ENTRY_SIZE)
    for b in backup["entries"]:
        with open(data_path(pck, b.get("orig_wem_file")), "rb") as original:
            orig = original.read()
        lut = bytes.fromhex(b.get("orig_lut_hex", ""))
        e = entries.get(b["fileID"])
        if len(lut) != 20 or len(orig) != b["size"] or not e or b["offset"] + b["size"] > length:
            raise ValueError("备份不完整或音乐包结构不匹配")
        fid, lang, size, offset = struct.unpack("<IIIQ", lut)
        if (fid, lang, size, offset, e[2], e[3]) != (b["fileID"], e[0], b["size"], b["offset"], b["offset"], b["lut_pos"]):
            raise ValueError("音乐包结构与备份不匹配")
        if b.get("orig_wem_sha256") and hashlib.sha256(orig).hexdigest() != b["orig_wem_sha256"].lower():
            raise ValueError("原曲备份校验失败")
        if b.get("replacement_file"):
            with open(data_path(pck, b["replacement_file"]), "rb") as replacement:
                new = replacement.read()
            if len(new) > len(orig) or hashlib.sha256(new).hexdigest() != b.get("replacement_sha256", "").lower():
                raise ValueError("替换记录校验失败")
            if any(v not in (old, target) for v, old, target in zip(struct.pack("<I", e[1]), lut[8:12], struct.pack("<I", len(new)))):
                raise ValueError("音乐索引已变化")
            new = new.ljust(len(orig), b"\0")
            f.seek(offset)
            current = f.read(size)
            if any(v not in (old, target) for v, old, target in zip(current, orig, new)):
                raise ValueError("音乐内容已被其他程序改动")
        elif e[1] > b["size"]:
            raise ValueError("旧备份与当前音乐容量不匹配")
    if backup.get("original_pck_sha256") and canonical_hash(f, pck, backup) != backup["original_pck_sha256"].lower():
        raise ValueError("音乐包版本与备份不匹配")


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
    with open_package(args.pck) as f:
        # 在独占写句柄内重读，避免检查与写入之间目标发生变化。
        e = next((item for item in read_entries(f) if item["fileID"] == args.file_id), None)
        if e is None or len(new) > e["size"]:
            raise ValueError("目标曲目已变化，请重新检查")
        if os.path.exists(bp):
            with open(bp, encoding="utf-8") as manifest:
                backup = json.load(manifest)
            if any(b["fileID"] == args.file_id for b in backup["entries"]):
                raise ValueError("原曲已替换，请先恢复")
        validate_backup(f, args.pck, backup)
        if backup.get("pck_length") is None:
            backup["pck_length"] = os.fstat(f.fileno()).st_size
        if not backup.get("original_pck_sha256"):
            backup["original_pck_sha256"] = canonical_hash(f, args.pck, backup)
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
            "orig_wem_sha256": hashlib.sha256(orig_wem).hexdigest(),
            "replacement_file": os.path.basename(bin_name + ".new"),
            "replacement_sha256": hashlib.sha256(new).hexdigest(),
        })
        atomic_bytes(bin_name, orig_wem, False)
        replacement_saved = False
        try:
            atomic_bytes(bin_name + ".new", new, False)
            replacement_saved = True
            save_backup(bp, backup)
        except BaseException:
            os.remove(bin_name)
            if replacement_saved:
                os.remove(bin_name + ".new")
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
    with open_package(args.pck) as f:
        with open(bp, encoding="utf-8") as manifest:
            backup = json.load(manifest)
        validate_backup(f, args.pck, backup)
        for b in list(backup["entries"]):
            bin_name = data_path(args.pck, b["orig_wem_file"])
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
                save_backup(bp, backup)
            else:
                os.remove(bp)
            os.remove(bin_name)
            if b.get("replacement_file"):
                os.remove(data_path(args.pck, b["replacement_file"]))
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
