# 解析 Wwise AKPK .pck，列出流式媒体条目（fileID / language / size / offset）
# 用法: python tools/pck_list.py <pck路径> [--json 输出路径]
import struct
import sys
import json


def parse_pck(path):
    with open(path, "rb") as f:
        magic, hsize, ver = struct.unpack("<4sII", f.read(12))
        assert magic == b"AKPK", f"not a pck: {path}"
        lang_sz, sb_sz, stm_sz, ext_sz = struct.unpack("<4I", f.read(16))
        lang_lut = f.read(lang_sz)
        # 语言表: 若干 {u32 id, u32 offset}，随后 UTF16 名称字符串
        langs = {}
        n = lang_sz // 8
        for i in range(n):
            lid, off = struct.unpack("<II", lang_lut[i * 8: i * 8 + 8])
            # offset 相对语言表起始
            end = lang_lut.find(b"\x00\x00", off)
            name = lang_lut[off:end].decode("utf-16-le", "replace")
            langs[lid] = name
        sb_lut = f.read(sb_sz)
        stm_lut = f.read(stm_sz)
        ext_lut = f.read(ext_sz)
        entries = []
        if len(stm_lut) >= 4:
            (count,) = struct.unpack("<I", stm_lut[:4])
            for i in range(count):
                e = stm_lut[4 + i * 20: 4 + (i + 1) * 20]
                if len(e) < 20:
                    break
                fid, langid, size, off = struct.unpack("<IIIQ", e)
                entries.append({
                    "fileID": fid,
                    "language": langs.get(langid, str(langid)),
                    "size": size,
                    "offset": off,
                })
        return entries


def main():
    path = sys.argv[1]
    entries = parse_pck(path)
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        with open(out, "w", encoding="utf-8") as f:
            json.dump({"pck": path, "count": len(entries), "entries": entries}, f, indent=1)
        print(f"{len(entries)} entries -> {out}")
    else:
        for e in entries:
            print(f"{e['fileID']:>12}  {e['language']:>6}  {e['size']:>10}  @{e['offset']}")


if __name__ == "__main__":
    main()
