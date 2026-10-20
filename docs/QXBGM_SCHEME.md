# QxBGM 方案格式 v1（qxbgm/v1）

千星BGM替换管理器（QxBgmManager）的**可分享替换方案**标准。任何人按此格式
存储/分享数据，App 即可一键读取、下载音频并完成本地替换。

## 分发形态（两种）

1. **单文件**：`<名字>.qxbgm.json` —— 纯清单，音频通过 `https://` URL 引用，
   导入时由 App 下载到本地缓存。
2. **打包文件**：`<名字>.qxbgmzip` —— ZIP 压缩包，内含 `manifest.json`（同单文件格式）
   和 `audio/` 目录（相对路径引用）。适合已编码 wem 的即装即用分享。

## manifest 格式

```jsonc
{
  "scheme": "qxbgm/v1",            // 必填，版本标识
  "id": "th01-bgm-pack",           // 必填，方案唯一ID（字母数字连字符）
  "name": "TH01 灵异传 BGM 包",     // 必填，显示名
  "author": "someone",
  "description": "……",
  "createdAt": "2026-10-19T00:00:00+08:00",
  "gameVersion": "7.1",            // 制作时的游戏版本（提示用，不强制校验）
  "wonderland": {                  // 可选，关联的奇域（地图）
    "id": "1073741828",
    "name": "科研存档",
    "url": "https://……"
  },
  "tracks": [                      // 必填，至少 1 条
    {
      "target": {
        "fileID": 122217526,       // 必填，目标条目（BeydMusic*.pck 内 fileID）
        "pck": "BeydMusic0.pck"    // 可选；省略时 App 按全库索引定位
      },
      "targetRef": {               // 可选，人类可读对照（展示+安装后校验提示）
        "index": 10071,
        "name": "难逃之阵"
      },
      "audio": {
        "uri": "audio/youkai_komachi.mp3",  // 必填：包内相对路径 / https URL / 本地绝对路径
        "kind": "raw",             // 必填：raw=原始音频需编码 | wem=已编码PTADPCM直接注入
        "sha256": "可选，十六进制，下载/读取后校验",
        "durationHintSec": 156.7   // 可选，展示用
      },
      "encode": {                  // kind=raw 时生效；kind=wem 时忽略
        "preset": "chiptune-6k",   // normal=直接编码 | chiptune-6k=先6kHz低通再编码
        "loop": false              // 是否写入全曲循环 smpl（默认 false，循环建议交给地图节点）
      }
    }
  ]
}
```

## App 导入流程（一键）

1. 校验 scheme 版本、必填字段、target 在全库存在、容量（wem/raw 编码后 ≤ 条目大小）。
2. 音频落地：URL → 下载到 `build/bgm-inject-research/schemes/<id>/audio/`；
   包内相对路径 → 从 zip 解出；本地绝对路径 → 直接使用。sha256 存在则校验。
3. `kind=raw` 的按 preset 排队编码（可取消）；`kind=wem` 的直接使用。
4. 用户勾选确认后逐条 patch（默认全选；已 patched 的条目标记跳过或先复原）。
5. 写入本地已安装方案登记（含每条 fileID 记录），用于后续"卸载方案"（= 对这些条目 restore）
   与版本更新前的批量复原。

## 导出流程（分享）

把当前活动补丁打包为 `.qxbgmzip`：从 pck 抽取各 patched 条目的当前 wem 字节作为
`kind: "wem"` 音频（接收方免编码），target/targetRef 取自补丁记录与曲名映射，
填写 name/author/description 后写出。

## 兼容与演进

- 未知顶层字段必须被读取方忽略（前向兼容）。
- `scheme` 版本不匹配的清单拒绝导入并提示。
- 音频 URI 支持 `https://`；`http://` 拒绝（安全）。
- 编码预设枚举：`normal`、`chiptune-6k`；未知预设按 normal 处理并警告。
