# QxBGM 方案的分发与订阅体系设计（无自建服务器，git 中心化索引模式）

> 目标：发布者共享、使用者下载 BGM 替换方案（qxbgm/v1），
> 不自建任何服务器。参照 BetterGI 脚本仓库模式（中央索引仓库 + fork/PR + 应用内订阅拉取）。

## 一、角色与总体结构

```
┌────────────────────┐   fork + PR(一条注册条目)   ┌──────────────────────┐
│ 发布者自己的 git 仓库 │ ─────────────────────────▶ │ 中心化索引仓库(唯一)   │
│ schemes/*.qxbgm.json │                            │ qxbgm-registry        │
│ Releases: *.qxbgmzip │                            │ index/<scheme-id>.json│
└─────────┬──────────┘                            └──────────┬───────────┘
          │ Releases/raw 下载音频                            │ git clone/pull(浅克隆)
          ▼                                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 使用者的 QxBgmManager：方案市场页浏览索引 → 一键安装 → 本地替换         │
└─────────────────────────────────────────────────────────────────────┘
```

- **中心化索引仓库**（唯一，如 `qxbgm-registry`）：只做"发现与管理"——每个方案一条注册条目 JSON，
  通过 PR 审核入库。不存音频。
- **发布者仓库**（各自管理）：方案的 manifest（.qxbgm.json）+ 音频资产（Git 仓库内小文件，
  或 Releases 附件大文件）。发布者用 git 版本化管理自己的奇域关联与方案迭代。
- **App（使用者）**：浅克隆索引仓库 → 本地浏览/搜索 → 按条目解析出 manifest 与音频 URL →
  下载 → 校验 →（必要时编码）→ 打入补丁 → 登记已安装版本。

## 二、仓库布局

### 2.1 索引仓库 qxbgm-registry

```
README.md                  # 入库指南（PR 流程）
registry.json              # {"version":1,"updated":"..."}
index/
  th01-bgm-pack.json       # 每个方案一条注册条目（文件名=scheme id）
  miliastra-8bit-pack.json
ci/                        # 校验脚本（schema/URL可达/sha256）
```

注册条目 `index/<id>.json`：

```jsonc
{
  "registry": "qxbgm-registry/v1",
  "id": "th01-bgm-pack",
  "name": "TH01 灵异传 BGM 包",
  "author": "github:crt106",
  "description": "……",
  "tags": ["touhou", "8bit", "全关卡"],
  "category": "音乐包",                  // 自由分类，市场页筛选用
  "version": "1.2.0",                    // 语义化版本，App 据此提示升级
  "updatedAt": "2026-10-20",
  "wonderland": { "id": "1073741828", "name": "科研存档" },
  "manifest": {
    "url": "https://gitee.com/<user>/th01-bgm/raw/main/schemes/th01-bgm-pack.qxbgm.json",
    "sha256": "……"                        // manifest 自身校验（可选但建议）
  },
  "package": {                           // 可选：整包下载（与 manifest 二选一或并存）
    "url": "https://gitee.com/<user>/th01-bgm/releases/download/v1.2.0/th01-bgm-pack.qxbgmzip",
    "sha256": "……",
    "size": 8460132
  },
  "tracks": 13,                          // 展示用统计
  "minAppVersion": "1.0.0"
}
```

### 2.2 发布者仓库

```
schemes/th01-bgm-pack.qxbgm.json   # qxbgm/v1 manifest，音频 uri 指向 release 附件
audio/                             # 小体积原始音频可直接入库（不推荐 >5MB）
README.md                          # 方案说明、适用奇域、曲目清单
Releases（仓库外）                  # *.qxbgmzip / *.wem / 音频包（大文件放这里）
```

manifest 里的 `audio.uri` 直接写 Releases 附件 URL 即可——**现有 qxbgm/v1 格式零改动**
（URL 下载链路已实现：https only + sha256 校验）。

## 三、关键链路

### 发布者上架
1. 在自己仓库维护 scheme（改曲子 = git commit，天然版本历史）。
2. 打 Release 上传 `.qxbgmzip` 或散音频。
3. fork 索引仓库 → 添加/更新 `index/<id>.json` → PR。
4. CI 自动校验：schema、URL 可达性（HEAD）、sha256 与实载一致、wonderland id 格式；
   维护者人工过一眼内容合规 → 合并即上架。

### 使用者安装
1. App 方案市场页：`git clone --depth 1`（首次）/ `git pull`（后续）索引仓库到本地缓存。
2. 浏览/搜索/按标签与奇域筛选；条目卡片显示名称/作者/版本/曲目数/更新时间。
3. 一键安装：下载 manifest（或整包）→ 校验 → 音频下载 → raw 编码 → 容量复核 →
   patch → 写入本地安装登记（含 registry 条目版本号）。

### 更新
App 每次拉取索引后比对已安装方案的 `version`：有新版 → 卡片上提示"可升级"，
升级 = 重新走安装流程（patched 条目先 restore 再 patch，由现有卸载/安装语义保证）。

## 四、信任与安全模型

- **纯数据无代码**：索引条目与 manifest 都是 JSON，App 不执行任何远程脚本；
  音频只做解码编码与字节注入，攻击面≈恶意音频（解码器崩溃级，无提权面）。
- **入库闸门**：PR 人工评审 + CI 校验（schema/可达/sha256）。恶意 URL 在 CI 阶段拦截。
- **传输**：仅 https；大文件强制 sha256（qxbgm/v1 已支持，索引条目同样要求）。
- **撤销**：维护者删条目即下架；App 对已安装的本地副本不受影响（可手动卸载）。

## 五、国内可达性（必须正面回答的问题）

| 资产 | 首选 | 备选 |
|---|---|---|
| 索引仓库托管 | **Gitee**（国内直连稳定） | GitHub（国际镜像，App 支持双远端切换） |
| manifest/小文件 | 仓库 raw（Gitee/GitHub 均可） | jsDelivr（GitHub 加速，失效则回退源站） |
| 音频/Releases 附件 | **Gitee Releases**（单文件≤1GB，国内快） | 发布者自管对象存储（七牛/腾讯COS免费额度） |
| 索引在 App 内镜像 | 本地浅克隆缓存目录，离线可读 | — |

不推荐 ghproxy 类公共代理作为正式链路（寿命不可控），只作设置里的自定义加速选项。

## 六、与既有分享形态的兼容

- `.qxbgmzip` 单包分享（QQ群/网盘直发）**继续有效**——方案管理页的"打开方案文件"不变。
- 索引条目 `package.url` 直接指向 `.qxbgmzip` 即可，格式零改动。
- 已安装方案登记增加 `registryId` 与 `registryVersion` 字段（可空）：
  空 = 手动安装的散包（不参与升级检查）；非空 = 来自市场的方案（参与升级检查）。

## 七、落地分期

- **P0（手动期，本周可跑）**：建好索引仓库（先建在我们账号下），按上述 schema 手写
  1~2 个真实方案条目（TH01 包、妖怪小町单曲包）；App 方案管理页加"打开索引目录"，
  直接读本地手动 clone 的索引仓库展示条目。零新代码链路验证。
- **P1（App 内订阅）**：新增「方案市场」页：LibGit2Sharp（BetterGI 同款依赖）浅克隆/拉取索引
  → 条目列表/搜索/筛选 → 一键安装（复用 SchemeService 导入链路）→ 版本升级提示。
- **P2（生态化）**：索引仓库 CI 校验脚本（Python，Gitee Go/GitHub Actions）、PR 模板、
  上架指南 README、可选的纯网页 PR 指引（参照 BetterGI 的网页端 fork 流程）。

## 八、为什么是这个模式（备选对比）

| 方案 | 优点 | 否决/入选理由 |
|---|---|---|
| **git 索引仓库（本方案）** | 零服务器、版本管理天然、PR=审核、生态可分叉 | 入选。BetterGI 已验证同形态可行 |
| QQ群/网盘散包 | 零门槛 | 保留为补充；无发现性、无版本追踪 |
| Gist/单文件聚合页 | 简单 | 无审核闸、无二进制资产托管、发现性差 |
| 对象存储 + 静态聚合页 | 快 | 需要有人长期管聚合页≈半个自建服务器 |
