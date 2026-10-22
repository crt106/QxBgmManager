"""文件头、压缩帧和 Python 写包入口回归；仅使用合成数据。"""
import argparse, importlib.util, json, struct, sys, tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'tools'), str(ROOT / 'vendor/python-deps')]
from wem_validate import validate_wem
from ptadpcm_enc import build_wem
import bgm_swap

checks = 0
def check(value, name):
    global checks
    assert value, name
    checks += 1
    print('通过', name)

mono = build_wem(1, bytes(36 * 2), 128, False)
stereo = build_wem(2, bytes(72 * 2), 128, False)
check(validate_wem(mono)['channels'] == 1, '单声道扩展配置正确')
check(validate_wem(stereo)['channels'] == 2, '双声道扩展配置正确')
check(validate_wem(build_wem(1, bytes(36 * 2), 65, True))['samples'] == 128, '不足一帧的音频与循环终点有效')
golden = b'RIFF'+struct.pack('<I',56+144)+b'WAVEfmt '+struct.pack('<IHHIIHHH6s',24,0x8311,2,48000,54000,72,4,6,bytes.fromhex('000002310000'))+b'JUNK'+struct.pack('<II',4,0)+b'data'+struct.pack('<I',144)+bytes(144)
check(stereo == golden, '双声道格式与原正确实现逐字节一致')

def changed(data, offset, value):
    b=bytearray(data); b[offset]=value; return bytes(b)
def sized(data):
    b=bytearray(data); struct.pack_into('<I',b,4,len(b)-8);return bytes(b)
bad = {
    '旧版崩溃头':changed(changed(mono,40,2),41,0x31),
    '反向声道不一致':changed(changed(stereo,40,1),41,0x41),
    '错误块对齐':changed(mono,32,72), '错误速率':changed(mono,28,0),
    '无效扩展长度':changed(mono,36,0), '头部截断':mono[:30],
    '数据截断':mono[:-1], '区块溢出':changed(mono,63,255),
    '首帧索引越界':changed(mono,68,12), '末帧索引越界':changed(mono,104,255),
    '重复格式':sized(mono+mono[12:44]), '重复数据':sized(mono+mono[56:]),
    '零数据':sized(mono[:60]+bytes(4)), '残缺尾部':sized(mono+b'\0'),
}
loop=build_wem(1,bytes(36),64,True)
bad['循环终点越界']=changed(loop,112,64)
for name,data in bad.items():
    rejected=False
    try: validate_wem(data)
    except ValueError: rejected=True
    check(rejected,'拒绝'+name)
padded=sized(mono[:44]+b'test'+struct.pack('<I',1)+b'x\0'+mono[44:])
check(validate_wem(padded)['channels']==1,'支持未知奇数区块的填充')
for args in [(3,bytes(108),64,False),(1,bytes(36),0,False),(1,bytes(35),64,False)]:
    rejected=False
    try: build_wem(*args)
    except ValueError: rejected=True
    check(rejected,'编码输出拒绝无效参数')
with tempfile.TemporaryDirectory(prefix='qxbgm-wem-') as directory:
    p=Path(directory); pck=p/'test.pck'; wem=p/'test.wem'
    original=bytearray(512);original[:4]=b'AKPK';struct.pack_into('<I',original,28,1)
    struct.pack_into('<IIIQ',original,32,123,0,256,128);pck.write_bytes(original)
    wem.write_bytes(bad['旧版崩溃头']);rejected=False
    try: bgm_swap.cmd_patch(argparse.Namespace(pck=str(pck),wem=str(wem),file_id=123))
    except ValueError: rejected=True
    check(rejected and pck.read_bytes()==original and not Path(str(pck)+'.bgmbackup').exists(),'Python 写包入口拒绝旧音频且不写包或备份')
    wem.write_bytes(mono)
    bgm_swap.cmd_patch(argparse.Namespace(pck=str(pck),wem=str(wem),file_id=123))
    check(pck.read_bytes()[128:128+len(mono)]==mono,'Python 写包入口接受修正单声道')
    # 调用与 CLI 相同的恢复函数。
    bgm_swap.cmd_restore(argparse.Namespace(pck=str(pck),file_id=None))
    check(pck.read_bytes()==original,'Python 安装恢复逐字节一致')
report={'passed':True,'checks':checks,'game_written':False}
out=ROOT/'build/verification/wem-validation-python.json';out.parent.mkdir(parents=True,exist_ok=True)
out.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('全部通过',checks)
