"""只使用合成音频，验证批量编码逐字节一致与解码逐采样一致。"""
import importlib.util
import json
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "vendor/python-deps"))
sys.path.insert(0, str(ROOT / "tools"))
import numpy as np


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


new = load("new_encoder", ROOT / "tools/ptadpcm_enc.py")


def decode_reference(frames):
    """逐帧、逐采样解码，作为批量解码的独立对照实现。"""
    import struct
    out = []
    for offset in range(0, len(frames), 36):
        frame = frames[offset:offset + 36]
        h2, h1, index = struct.unpack("<hhB", frame[:5])
        out.extend((h2, h1))
        for i in range(62):
            packed = frame[5 + i // 2]
            nibble = (packed >> 4) & 15 if i & 1 else packed & 15
            sample = max(-32768, min(32767, int(new.STEP_TAB[index, nibble]) + 2 * h1 - h2))
            index = int(new.NEXT_TAB[index, nibble])
            out.append(sample)
            h2, h1 = h1, sample
    return np.asarray(out, dtype=np.int32)


rng = np.random.default_rng(12345)
cases = [rng.integers(-32768, 32768, n, dtype=np.int32) for n in (1, 63, 64, 65, 2111)]
cases += [np.zeros(2112, dtype=np.int32), np.tile(np.array([-32768, 32767], dtype=np.int32), 1056)]
for i, samples in enumerate(cases):
    for beam, ef in ((8, 0), (64, 0), (64, 0.7)):
        expected = new._encode_channel_beam_reference(samples, beam, ef)
        actual = new.encode_channel_beam(samples, beam, ef)
        assert np.array_equal(expected, actual), ("编码字节不一致", i, beam, ef)
        assert np.array_equal(decode_reference(expected.tobytes()), new.decode_channel(actual.tobytes()))
print("PASS：21 组编码逐字节一致，解码逐采样一致")
samples = (np.sin(np.arange(24000) * (440 * 2 * np.pi / 48000)) * 20000).astype(np.int32)
started = time.perf_counter()
expected = new._encode_channel_beam_reference(samples, 64)
old_seconds = time.perf_counter() - started
started = time.perf_counter()
actual = new.encode_channel_beam(samples, 64)
new_seconds = time.perf_counter() - started
assert np.array_equal(expected, actual)
results = dict(samples=len(samples), beam=64, old_seconds=old_seconds, new_seconds=new_seconds,
               speedup=old_seconds / new_seconds, byte_identical=True)
print(json.dumps(results, indent=2))
report_dir = ROOT / "build/verification"
report_dir.mkdir(parents=True, exist_ok=True)
(report_dir / "codec-benchmark.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
