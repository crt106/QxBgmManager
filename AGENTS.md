# AGENTS.md — QxBgmManager 开发指南（面向 AI Agent / 新协作者）

千星BGM替换管理器：WPF (.NET 10) 桌面应用，在原神·千星奇域客户端的本地音频资产
（`AudioAssets/BeyondUGC/BeydMusic0-7.pck`，Wwise AKPK 容器，共 188 轨）上做
**原位 BGM 替换**：自研 PTADPCM 编解码（Python 子进程）→ 容量校验 → 带备份打补丁，
支持 qxbgm/v1 方案导入/导出与 git 中心化方案市场。

- 使用者文档：[`README.md`](README.md)
- 方案格式规范：[`docs/QXBGM_SCHEME.md`](docs/QXBGM_SCHEME.md)
- 分发体系设计：[`docs/QXBGM_REGISTRY.md`](docs/QXBGM_REGISTRY.md)

## 仓库结构

```
app/                     主程序（WPF，net10.0-windows）
  App.xaml(.cs)          入口：加载设置、主题（WPF-UI Fluent + mint 色）、渲染模式降级
  MainWindow.xaml(.cs)   FluentWindow + NavigationView 页面路由；进主菜单后静默检查更新
  Pages/                 7 个页面（XAML + code-behind）：EnvCheck / Library / ReplaceCenter
                         / Convert / Market / Schemes / Settings / WonderBrowse
  ViewModels/            每页一个 VM（CommunityToolkit.Mvvm，[ObservableProperty]/[RelayCommand]）
  Services/              全部业务逻辑（见下文模块表）
  Models/                POCO：AppSettings / PckModels / SchemeModels / RegistryModels / TrackInfo…
  Converters/            XAML 值转换器
  Tests/                 自测控制台项目（从主程序编译中排除，见 csproj 的 Compile Remove）
    SchemeTests/         方案/曲库/备份/转码离线自测（自研 Check 断言，非 xunit）
    UiTests/             WPF 布局、滚轮、页面进度检查（STA 起 App）
    codec_regression.py  编码字节一致性 / 解码一致性 / 速度对照（纯合成音频）
  appsettings.json       用户设置（跟工程走，不随 bin 漂移；含本机路径，勿提交个人配置）
  data/                  运行时数据（已 gitignore）：方案安装登记、索引仓库浅克隆缓存
tools/                   Python 工具链（App 通过子进程调用）
  ptadpcm_enc.py         PTADPCM (fmt 0x8311) 编解码器：encode/decode/selftest，beam64 搜索
  bgm_swap.py            pck list/extract/patch/restore/status（C# PckService 的语义蓝本）
  track_matcher.py       曲名映射构建（时长匹配 + 音频指纹）
  pck_list.py            pck 条目列举（研究用）
scripts/setup.ps1        开发环境一键准备（便携 .NET SDK + numpy）
build/                   工作产物（gitignore）：bgm-inject-research/ 编码输出与方案缓存、
                         verification/ 自测截图与报告
vendor/                  setup.ps1 生成的本地工具链（gitignore）：dotnet/、python-deps/
docs/                    格式规范与分发体系设计文档
```

生态仓库（非本仓库）：`qxbgm-registry`（中心化方案索引 + 共享配置）、
`qxbgm-schemes-touhou8bit`（发布者仓库示例）。

## 开发环境与常用命令

```powershell
# 0. 环境准备（便携 .NET 10 SDK → vendor\dotnet，numpy → vendor\python-deps，不污染系统）
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1

# 1. 构建（解决方案含主程序 + 两个测试项目）
vendor\dotnet\dotnet.exe build app\QxBgmManager.sln

# 2. 离线自测（改了 Services/Scheme/Pck/编码链路后必跑）
vendor\dotnet\dotnet.exe run --project app\Tests\SchemeTests

# 3. 编码回归（改了 tools\ptadpcm_enc.py 或编码参数后必跑）
python app\Tests\codec_regression.py

# 4. UI 自测（改了页面布局/滚动后跑；会弹临时窗口，产物到 build\verification\）
vendor\dotnet\dotnet.exe run --project app\Tests\UiTests

# 5. 自包含单文件发布
vendor\dotnet\dotnet.exe publish app\QxBgmManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app\publish
```

外部依赖：Python 3.10+（numpy）、ffmpeg（PCM 解码）。缺失时测试会输出 `SKIP` 而非失败
——看到 SKIP 是环境缺项，不是代码问题；判断测试是否通过看有无 `FAIL` 与退出码。

## 架构：MVVM + 静态 Service

数据流：`Pages (XAML + code-behind)` ↔ `ViewModels` → `Services`（静态类为主）→ 文件系统 / 子进程 / git。

### Services 模块表（改哪里找哪里）

| 服务 | 职责 | 关键语义 |
|---|---|---|
| `PckService` | AKPK pck 读写 | patch 前先落备份再原子提交清单；restore 校验通过才清记录；与 bgm_swap.py 备份格式逐字段互认 |
| `SchemeService` | qxbgm/v1 方案 | manifest 解析校验（未知字段忽略=前向兼容，版本不符拒绝，http 拒绝）、音频落地（URL/zip/本地/基地址）、安装单轨、导出 .qxbgmzip |
| `RegistryService` | 方案市场 | LibGit2Sharp 浅克隆索引仓库、条目解析、语义化版本比较、共享配置（track-map/app-config）下载 |
| `EncodeService` | 编解码子进程 | 封装 `python tools\ptadpcm_enc.py`；单飞行闸（同一时间一个子进程）；解析 `QXBGM_PROGRESS {json}` 进度行；取消即 Kill 整棵进程树 |
| `AppState` | 跨页共享缓存 | 8 个 pck 全量条目 + 曲名映射 + 补丁标记；`EnsureLibraryAsync` 带 SemaphoreSlim 去重；patch 后须 `InvalidateLibrary()` |
| `SettingsService` | 设置与定位 | `FindWorkspaceRoot()` 向上找 `tools\ptadpcm_enc.py` 定位仓库根；游戏根目录探测；Python/ffmpeg 解析 |
| `TrackMapService` | fileID→曲名 | 三级降级：registry 共享缓存 → 本地 track-map.json → 2 条硬编码种子，绝不抛异常 |
| `AppDataService` | 登记持久化 | `app\data\` 下 JSON 读写（安装登记等） |
| `EnvCheckService` | 首启闸门 | 8 项环境检查，6 项 Critical 全过才能进主菜单 |
| `UpdateService` | 自动更新 | GitHub Releases 固定源 → cmd 脚本自替换重启 |
| `AudioPreviewService` | 试听 | MediaPlayer 单例，同一时间只播一条 |
| `GameProcessService` | 游戏检测 | YuanShen.exe 是否运行（热替换/占用提示） |
| `ScrollBehavior` | 滚轮传递 | 附加属性：内层列表先滚，到边界再滚页面 |

### 页面 ↔ ViewModel 对应

EnvCheck / Library（曲库，全量缓存 + ListCollectionView 过滤，性能敏感）/ ReplaceCenter（替换中心）/
Convert（编码转换）/ Market（方案市场）/ Schemes（方案管理）/ Settings / WonderBrowse（内嵌 WebView2）。
页面导航集中在 `MainWindow.NavigateTo`，落地页固定为 EnvCheckPage。

## 必须遵守的约定与不变量

1. **PCK 写入安全是红线**。patch 顺序固定：备份音频字节 → 原子写清单 → 改 pck 数据 → 改 LUT；
   restore 反向且带回读校验。任何改动不得破坏"任意一步中断后可安全重试恢复"的性质。
   `PatchWithCheckpoint` 的故障注入仅供离线自测使用。
2. **测试不得触碰真实游戏 pck**。备份/恢复测试用合成 PCK（`PckSafetyTests`）；真实转码与曲库
   检查依赖本机环境，缺失时明确 `SKIP`。也不要把 `appsettings.json` 里的个人本机路径"修复"掉。
3. **子进程输出在后台线程回调**。`EncodeService` 的 onLine / onProgress 回调不在 UI 线程，
   更新 UI 前必须经 `App.Dispatch` 切回。子进程环境已强制 `PYTHONIOENCODING=utf-8`，
   新增 Python 输出保持 UTF-8，进度走 `QXBGM_PROGRESS {json}` 协议（`EncodeProgress.TryParse`）。
4. **双实现保持一致**。`PckService` 是 `bgm_swap.py` 的 C# 移植，备份文件格式两侧互认；
   改动任一侧的备份字段（fileID/offset/size/lut_pos/orig_wem_*）必须同步另一侧并跑两侧验证。
5. **语言与文案**。代码注释、UI 文案、提交信息、测试输出全部使用中文；提交信息风格参照
   git log（`修复…`、`feat: …`、`ux: …`，一句话概括）。
6. **格式前向兼容**。qxbgm/v1 与 qxbgm-registry/v1 的未知字段必须被忽略；只收 https；
   sha256 有则必校验。改 manifest 结构先读 `docs/QXBGM_SCHEME.md`。
7. **gitignore 语义**。`app/data/`、`build/`、`vendor/` 是运行时/本机产物，不入库；
   新增产物目录记得评估是否要加 ignore。
8. **性能敏感路径**。曲库 188 行用固定行高 + 可见区域渲染 + 分类切换复用视图
   （SchemeTests 第 6 节断言切换/搜索 < 50ms）；改动 LibraryViewModel 的过滤链路后跑该节。

## 改动后的验证清单

- 改 `Services/` 或方案/曲库逻辑 → 跑 SchemeTests（无 FAIL 即过）。
- 改 `tools/ptadpcm_enc.py` 或编码参数 → 跑 `codec_regression.py`（逐字节一致断言）。
- 改 PckService 写入路径 → 额外确认 PckSafetyTests 全过（含中断恢复用例）。
- 改页面/布局/滚动 → 跑 UiTests；有视觉产物可看 `build/verification/`。
- 改主程序版本号：`app/QxBgmManager.csproj` 的 `<Version>`（自动更新按 GitHub Releases 判断）。

## 已知本机默认值（不要当 bug 修）

- `SettingsService.DefaultFfmpegPath` 与 `KnownGameRoots` 是作者机器的默认值，属刻意兜底。
- `FindWorkspaceRoot` 找不到仓库时回退 `D:\ProgramSource2\th01_BeyondLocal`，同理。
- Mica/Backdrop 在 MainWindow 被关闭（NVIDIA 黑屏规避）；`RenderHardware=false` 时降级软件渲染。
