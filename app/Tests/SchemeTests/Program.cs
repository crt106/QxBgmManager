using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using QxBgmManager.Models;
using QxBgmManager.Services;

// qxbgm/v1 方案格式离线自测：解析、校验、zip 打包/解包往返、字段前向兼容。
// 不触碰游戏 pck 文件，不写登记；临时产物放系统临时目录。

int failures = 0;
void Check(bool ok, string msg)
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + msg);
    if (!ok) failures++;
}

// 定位工作区根（含 tools\ptadpcm_enc.py）与自测用 wem
string root = FindWorkspaceRoot();
string wemPath = Path.Combine(root, "build", "bgm-inject-research", "youkai_komachi_v2.wem");
Check(File.Exists(wemPath), $"自测 wem 存在：{wemPath}");
string wemHash = await SchemeService.Sha256FileAsync(wemPath);
long wemSize = new FileInfo(wemPath).Length;

// ---- 1. 内存 manifest（2 条 tracks，一条 kind=wem 本地绝对路径）+ 未知字段前向兼容 ----
string manifestJson = $$"""
{
  "scheme": "qxbgm/v1",
  "id": "selftest-pack",
  "name": "自测方案",
  "author": "tester",
  "description": "离线自测",
  "futureTopLevel": { "anything": [1, 2, 3] },
  "tracks": [
    {
      "target": { "fileID": 122217526, "pck": "BeydMusic0.pck" },
      "targetRef": { "index": 10071, "name": "难逃之阵" },
      "audio": { "uri": "{{wemPath.Replace("\\", "\\\\")}}", "kind": "wem", "sha256": "{{wemHash}}", "durationHintSec": 99.9 },
      "futureTrackField": "ignored"
    },
    {
      "target": { "fileID": 15673379 },
      "targetRef": { "index": 10140, "name": "无尽的挑战" },
      "audio": { "uri": "audio/normal.mp3", "kind": "raw" },
      "encode": { "preset": "chiptune-6k", "loop": true },
      "unknownNested": { "x": 1 }
    }
  ]
}
""";

SchemeManifest manifest;
try
{
    manifest = SchemeService.ParseManifest(manifestJson);
    Check(true, "前向兼容：未知顶层/条目字段被忽略且解析成功");
}
catch (Exception ex)
{
    manifest = new SchemeManifest();
    Check(false, "前向兼容解析：" + ex.Message);
}
Check(manifest.Id == "selftest-pack" && manifest.Name == "自测方案", "id/name 解析正确");
Check(manifest.Tracks.Count == 2, $"tracks = {manifest.Tracks.Count}（预期 2）");
Check(manifest.Tracks[0].Audio.Kind == "wem" && manifest.Tracks[0].Audio.Sha256 == wemHash, "kind=wem + sha256 解析正确");
Check(manifest.Tracks[1].Encode is { Preset: "chiptune-6k", Loop: true }, "encode.preset/loop 解析正确");
Check(manifest.Tracks[1].Target.Pck == null, "省略 pck 时按全库定位（target.pck 为 null）");

// 未知预设按 normal 处理并警告
var weird = SchemeService.ParseManifest(manifestJson.Replace("\"chiptune-6k\"", "\"ultra-hd\""));
Check(weird.Tracks[1].Encode!.Preset == "normal" && weird.Warnings.Count == 1,
    "未知编码预设按 normal 处理并给出警告");

// ---- 2. 版本不匹配拒绝 / http 拒绝 ----
try { SchemeService.ParseManifest(manifestJson.Replace("qxbgm/v1", "qxbgm/v2")); Check(false, "scheme 版本不匹配应拒绝"); }
catch (SchemeException) { Check(true, "scheme 版本不匹配拒绝导入"); }

string escapedWemPath = wemPath.Replace("\\", "\\\\");
try { SchemeService.ParseManifest(manifestJson.Replace(escapedWemPath, "http://evil.example.com/a.mp3")); Check(false, "http:// 应拒绝"); }
catch (SchemeException) { Check(true, "http:// 音频地址拒绝"); }

try { SchemeService.ParseManifest(manifestJson.Replace("\"selftest-pack\"", "\"bad id!\"")); Check(false, "非法 id 应拒绝"); }
catch (SchemeException) { Check(true, "非法 id（含空格/符号）拒绝"); }

// ---- 3. 目标定位与校验（需要真实曲库；无游戏安装时跳过库相关断言） ----
var gameRoot = SettingsService.DetectGameRoots().FirstOrDefault();
if (gameRoot != null)
{
    var library = new List<PckEntry>();
    foreach (var pck in SettingsService.MusicPckPaths(gameRoot))
        library.AddRange(PckService.ListEntries(pck));
    Check(library.Count > 0, $"曲库加载 {library.Count} 条");

    var e1 = SchemeService.FindTarget(manifest.Tracks[0], library);
    Check(e1 != null && e1.PckName == "BeydMusic0.pck" && e1.FileId == 122217526,
        "按 pck+fileID 定位目标条目");
    var e2 = SchemeService.FindTarget(manifest.Tracks[1], library);
    Check(e2 != null && e2.FileId == 15673379, "省略 pck 时全库索引定位目标条目");
    bool patched1 = PckService.IsEntryPatched(e1!.PckPath, e1.FileId);
    Console.WriteLine($"      容量复核参考：wem {wemSize:N0} B vs 条目当前 {e1.Size:N0} B" + (patched1 ? "（条目已补丁，LUT size 为新 wem 大小，复核以原始备份为准）" : ""));
    Check(wemSize <= e1.Size || patched1, "容量可复核（未补丁时须 wem ≤ 条目容量）");
}
else
{
    Console.WriteLine("SKIP  未探测到游戏安装，跳过曲库相关断言");
}

// ---- 4. zip 打包/解包往返 ----
string tmpDir = Path.Combine(Path.GetTempPath(), "qxbgm-scheme-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpDir);
try
{
    string zipPath = Path.Combine(tmpDir, "roundtrip.qxbgmzip");
    // 手工造包验证 SchemeService 的读取/解析/解包链路（打包写出侧由生产代码 ExportSchemeZip 负责，
    // 这里不碰真实 pck，音频字节取自 komachi wem 文件本身）
    await CreateZipManually(zipPath, manifest, wemPath);
    var loaded = SchemeService.LoadManifestFile(zipPath);
    Check(loaded.Id == "selftest-pack" && loaded.Tracks.Count == 2, "qxbgmzip 读回 manifest 成功");
    // track0：本地绝对路径 + sha256 → 直接使用并通过校验
    var direct = await SchemeService.ResolveAudioAsync(loaded, loaded.Tracks[0], null, null, default);
    Check(await SchemeService.Sha256FileAsync(direct) == wemHash, "本地绝对路径音频直用 + sha256 校验通过");
    // track1：包内相对路径 → 从 zip 解出
    var resolved = await SchemeService.ResolveAudioAsync(loaded, loaded.Tracks[1], zipPath, null, default);
    Check(new FileInfo(resolved).Length == wemSize, "包内相对路径音频解出且字节数一致");
    Check(await SchemeService.Sha256FileAsync(resolved) == wemHash, "解出音频 sha256 与源一致");
    // 相对路径音频在 zip 缺失时报错
    var missing = SchemeService.ParseManifest(manifestJson);
    missing.Tracks[1].Audio.Uri = "audio/not-there.bin";
    try { await SchemeService.ResolveAudioAsync(missing, missing.Tracks[1], zipPath, null, default); Check(false, "zip 缺条目应报错"); }
    catch (SchemeException) { Check(true, "zip 内缺少音频条目时报错"); }
    // sha256 不符报错
    var badManifest = SchemeService.ParseManifest(manifestJson.Replace(wemHash, new string('0', 64)));
    try { await SchemeService.ResolveAudioAsync(badManifest, badManifest.Tracks[0], null, null, default); Check(false, "sha256 不符应报错"); }
    catch (SchemeException) { Check(true, "sha256 校验失败时报错中止"); }
}
finally
{
    try { Directory.Delete(tmpDir, true); } catch { }
}

// ---- 5. track-map 扩展格式解析（新格式 / 旧格式 / 未知字段 / 文件缺失降级） ----
string mapTmp = Path.Combine(Path.GetTempPath(), "qxbgm-trackmap-selftest-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    const string newFormat = """
    {"tracks":[
      {"index":10071,"name":"难逃之阵 Against the Invisible Net","fileID":122217526,
       "pck":"BeydMusic0.pck","durationSec":256,"match":"fingerprint","score":0.88,
       "category":103,"categoryName":"专辑测试","album":"原神-测试专辑","futureField":{"x":1}},
      {"index":10072,"name":"Test Track","fileID":42424242,"category":101}
    ]}
    """;
    await File.WriteAllTextAsync(mapTmp, newFormat);
    var loadedMap = TrackMapService.Load(mapTmp);
    Check(loadedMap.Count == 3, $"扩展格式映射加载 {loadedMap.Count} 条（文件条目覆盖同 fileID 种子后去重）");
    var t1 = TrackMapService.InfoOf(loadedMap, 122217526)!;
    Check(t1.Index == 10071 && t1.Category == 103 && t1.DisplayCategory == "任务音乐"
          && t1.Album == "原神-测试专辑" && Math.Abs(t1.DurationSec - 256) < 0.01,
        "扩展字段（index/category/durationSec/album）解析正确");
    Check(t1.PrimaryName == "难逃之阵" && t1.SplitName().English == "Against the Invisible Net",
        "中英曲名拆分正确");
    Check(t1.CategoryColor == "#F2C94C", "分类徽标颜色（103 任务=黄）");
    var t2 = TrackMapService.InfoOf(loadedMap, 42424242)!;
    Check(t2.DisplayCategory == "探索音乐" && t2.CategoryColor == "#6EE7B7", "仅 category 数字时回退默认分类名（101 探索=绿）");
    Check(TrackMapService.InfoOf(loadedMap, 999999) == null, "无映射返回 null（UI 按未识别处理）");
    Check(TrackMapService.NameOf(loadedMap, 999999) == "999999", "无映射 NameOf 回退 fileID");

    // 旧格式兼容（仅 index/name/fileID）+ 顶层数组
    const string oldFormat = """
    [{"index":10140,"name":"无尽的挑战","fileID":15673379}]
    """;
    await File.WriteAllTextAsync(mapTmp, oldFormat);
    var oldMap = TrackMapService.Load(mapTmp);
    Check(oldMap[15673379].Name == "无尽的挑战" && oldMap[15673379].Category == 0
          && oldMap[15673379].DisplayCategory == "未识别",
        "旧格式（顶层数组、仅 index/name/fileID）兼容且按未识别分类");

    // 文件缺失 → 仅种子，不抛异常
    var missingMap = TrackMapService.Load(Path.Combine(Path.GetTempPath(), "qxbgm-no-such-file.json"));
    Check(missingMap.Count == 2 && missingMap.ContainsKey(122217526), "映射文件缺失时降级为仅种子");
}
finally
{
    try { File.Delete(mapTmp); } catch { }
}

// ---- 6. 曲库过滤性能（真实 LibraryViewModel：全量缓存 + 只换过滤视图） ----
var detectRoot = SettingsService.DetectGameRoots().FirstOrDefault();
if (detectRoot != null)
{
    SettingsService.Current.GameRoot = detectRoot;
    var vm = new QxBgmManager.ViewModels.LibraryViewModel(); // ctor 自动加载
    for (int i = 0; i < 300 && vm.AllRowsCount == 0; i++) await Task.Delay(50);
    Check(vm.AllRowsCount == 188, $"曲库 VM 加载 {vm.AllRowsCount} 行");

    // 数据加载（pck 解析 + 映射）与行 VM 构建的 VM 侧分布
    var swLoad = Stopwatch.StartNew();
    var (loadOk, _) = await AppState.EnsureLibraryAsync(detectRoot, force: true);
    swLoad.Stop();
    var swBuild = Stopwatch.StartNew();
    var builtRows = AppState.Library
        .Select(e => new QxBgmManager.ViewModels.TrackRow(e, AppState.InfoOf(e.FileId),
            AppState.PatchedIds(e.PckPath).Contains(e.FileId)))
        .ToList();
    swBuild.Stop();
    Console.WriteLine($"      [Perf] VM 侧分布：pck 解析+映射 {swLoad.ElapsedMilliseconds} ms（ok={loadOk}），"
                      + $"188 行 TrackRow 构建 {swBuild.Elapsed.TotalMilliseconds:F1} ms（渲染/布局在 UI 线程，软渲染下另行发生）");

    var cats = vm.Categories.ToList();
    // 预热（JIT + 首次绑定）
    foreach (var c in cats) vm.SelectedCategory = c;
    vm.SelectedCategory = cats[0];

    const int cycles = 30;
    var sw2 = Stopwatch.StartNew();
    for (int i = 0; i < cycles; i++)
        vm.SelectedCategory = cats[i % cats.Count];  // 含 ApplyFilters
    sw2.Stop();
    double avgSwitch = sw2.Elapsed.TotalMilliseconds / cycles;
    Console.WriteLine($"      [Perf] 分类切换 {cycles} 次平均 {avgSwitch:F2} ms/次（目标 <50 ms）");
    Check(avgSwitch < 50, $"分类切换 {avgSwitch:F2} ms/次 < 50 ms");

    vm.SearchText = "10071";   // setter 走防抖，这里直接再调一次 ApplyFilters 测纯过滤
    sw2.Restart();
    for (int i = 0; i < cycles; i++) vm.ApplyFilters();
    sw2.Stop();
    double avgSearch = sw2.Elapsed.TotalMilliseconds / cycles;
    vm.SearchText = "";
    Console.WriteLine($"      [Perf] 搜索过滤 {cycles} 次平均 {avgSearch:F2} ms/次");
    Check(avgSearch < 50, $"搜索过滤 {avgSearch:F2} ms/次 < 50 ms");

    // 模拟旧行为：每次切页全量重建 188 个 TrackRow + 新集合（刷新成本对比）
    var entries = new List<QxBgmManager.Models.PckEntry>();
    foreach (var pck in SettingsService.MusicPckPaths(detectRoot))
        entries.AddRange(PckService.ListEntries(pck));
    sw2.Restart();
    for (int i = 0; i < 20; i++)
    {
        var rebuilt = entries
            .Select(e => new QxBgmManager.ViewModels.TrackRow(e, AppState.InfoOf(e.FileId),
                AppState.PatchedIds(e.PckPath).Contains(e.FileId)))
            .ToList();
        _ = new ObservableCollection<QxBgmManager.ViewModels.TrackRow>(rebuilt);
    }
    sw2.Stop();
    double avgRebuild = sw2.Elapsed.TotalMilliseconds / 20;
    Console.WriteLine($"      [Perf] 旧行为全量重建 188 行平均 {avgRebuild:F2} ms/次（新实现切页不再重刷）");
}
else
{
    Console.WriteLine("SKIP  未探测到游戏安装，跳过曲库性能自测");
}

// ---- 7. 环境检查项单元自测 ----
Check(EnvCheckService.CheckDirWritable(Path.GetTempPath()), "目录可写自测：临时目录通过");
Check(!EnvCheckService.CheckDirWritable(Path.Combine(Path.GetTempPath(), "qxbgm-no-such-dir")),
    "目录可写自测：不存在目录返回 false");
Check(EnvCheckService.ParsePythonVersion("3.13.7 (tags/v3.13.7:0493998240a)\n[MSC v.1943 64 bit (AMD64)]") == "3.13.7",
    "Python 版本解析");
Check(EnvCheckService.ParsePythonVersion("garbage output") == null, "Python 版本无法解析返回 null");
var envItems = EnvCheckService.CreateItems();
Check(envItems.Count == 8 && envItems.Count(i => i.Level == EnvCheckLevel.Critical) == 6
      && envItems.Count(i => i.Level == EnvCheckLevel.Optional) == 2,
    "检查项 8 项（●关键 6 / ○可选 2）");
Check(!EnvCheckService.AllCriticalPassed(envItems), "全部 Pending 时不可进入");
foreach (var i in envItems.Where(i => i.Level == EnvCheckLevel.Critical)) i.Status = EnvCheckStatus.Pass;
Check(EnvCheckService.AllCriticalPassed(envItems), "关键项全 Pass 后可进入");
envItems[0].Status = EnvCheckStatus.Fail;
Check(!EnvCheckService.AllCriticalPassed(envItems), "任一关键项 Fail 不可进入");
envItems[0].Status = EnvCheckStatus.Warn;
Check(EnvCheckService.AllCriticalPassed(envItems), "关键项 Warn（如 track-map 缺失）可进入");

// ---- 8. 方案市场：索引条目解析 / 版本比较 / 相对 URI 基地址 ----
const string fullEntry = """
{
  "registry": "qxbgm-registry/v1",
  "id": "test-pack",
  "name": "自测条目",
  "author": "tester",
  "description": "全字段",
  "tags": ["touhou", "8bit"],
  "category": "音乐包",
  "version": "1.2.0",
  "updatedAt": "2026-10-20",
  "wonderland": { "id": "1073741828", "name": "科研存档" },
  "manifest": { "url": "https://example.com/pack.qxbgm.json", "sha256": "abc" },
  "package": { "url": "https://example.com/pack.qxbgmzip", "sha256": "def", "size": 8460132 },
  "tracks": 13,
  "minAppVersion": "1.0.0",
  "futureField": 1
}
""";
var pe = RegistryService.ParseEntry(fullEntry);
Check(pe.Id == "test-pack" && pe.Version == "1.2.0" && pe.Package is { Size: 8460132 }
      && pe.Wonderland?.Name == "科研存档" && pe.Tags.Count == 2,
    "索引条目全字段解析（含未知字段忽略）");
var minimalEntry = RegistryService.ParseEntry(
    """{"registry":"qxbgm-registry/v1","id":"mini","name":"最小"}""");
Check(minimalEntry.Package == null && minimalEntry.Wonderland == null && minimalEntry.Tracks == 0,
    "索引条目缺省字段（无 package/wonderland/tracks）解析");
try { RegistryService.ParseEntry(fullEntry.Replace("qxbgm-registry/v1", "qxbgm-registry/v2")); Check(false, "条目版本不符应拒绝"); }
catch (SchemeException) { Check(true, "索引条目 registry 版本不匹配拒绝"); }

// LoadEntries：单条损坏跳过并警告
string idxDir = Path.Combine(Path.GetTempPath(), "qxbgm-registry-selftest-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(idxDir, "index"));
    await File.WriteAllTextAsync(Path.Combine(idxDir, "index", "a.json"), fullEntry);
    await File.WriteAllTextAsync(Path.Combine(idxDir, "index", "b.json"),
        """{"registry":"qxbgm-registry/v1","id":"mini2","name":"最小2"}""");
    await File.WriteAllTextAsync(Path.Combine(idxDir, "index", "broken.json"), "{ not json");
    var (list, warns) = RegistryService.LoadEntries(idxDir);
    Check(list.Count == 2 && warns.Count == 1 && list.Any(e => e.Id == "mini2"),
        $"LoadEntries 容错：2 条有效 + 1 条损坏跳过警告（实际 {list.Count}/{warns.Count}）");
}
finally { try { Directory.Delete(idxDir, true); } catch { } }

// 版本比较：1.0.0 < 1.0.1 < 1.2.0 < 1.10.0
Check(RegistryService.CompareVersions("1.0.0", "1.0.1") < 0
      && RegistryService.CompareVersions("1.0.1", "1.2.0") < 0
      && RegistryService.CompareVersions("1.2.0", "1.10.0") < 0
      && RegistryService.CompareVersions("1.10.0", "1.9.9") > 0
      && RegistryService.CompareVersions("1.2.0", "1.2.0") == 0
      && RegistryService.CompareVersions("1.2", "1.2.0") == 0,
    "语义化版本比较 1.0.0 < 1.0.1 < 1.2.0 < 1.10.0");
Check(RegistryService.IsUpgradeAvailable("1.0.0", "1.2.0")
      && !RegistryService.IsUpgradeAvailable("1.2.0", "1.0.0")
      && !RegistryService.IsUpgradeAvailable(null, "1.2.0"),
    "升级判定：已安装且条目更新才成立，未安装不计升级");

// 相对音频 URI 基地址解析
Check(SchemeService.ResolveAudioLocation("audio/a.mp3", "https://example.com/schemes/pack.qxbgm.json")
      == "https://example.com/schemes/audio/a.mp3",
    "https manifest 基地址：相对 URI 拼接正确");
string localManifest = Path.Combine(Path.GetTempPath(), "qxbgm-manifest-dir", "pack.qxbgm.json");
Check(SchemeService.ResolveAudioLocation("audio/a.mp3", localManifest)
      == Path.Combine(Path.GetTempPath(), "qxbgm-manifest-dir", "audio", "a.mp3"),
    "本地 manifest 文件路径基地址：相对 URI 解析到同目录");
Check(SchemeService.ResolveAudioLocation("audio/a.mp3", Path.GetTempPath())
      == Path.Combine(Path.GetTempPath(), "audio", "a.mp3"),
    "本地目录基地址：相对 URI 解析到目录下");
Check(SchemeService.ResolveAudioLocation("https://cdn.example.com/b.wem", "https://example.com/x.json")
      == "https://cdn.example.com/b.wem",
    "绝对 URI 不被基地址改写");

// 端到端：本地相对音频经 baseUri 落地 + sha256
string e2eDir = Path.Combine(Path.GetTempPath(), "qxbgm-baseuri-e2e-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(e2eDir, "audio"));
    string srcWem = Path.Combine(root, "build", "bgm-inject-research", "youkai_komachi_v2.wem");
    string relAudio = Path.Combine(e2eDir, "audio", "komachi.wem");
    File.Copy(srcWem, relAudio);
    string localJson = JsonSerializer.Serialize(new
    {
        scheme = "qxbgm/v1", id = "baseuri-test", name = "基地址自测",
        tracks = new[]
        {
            new
            {
                target = new { fileID = 122217526u },
                audio = new { uri = "audio/komachi.wem", kind = "wem",
                    sha256 = await SchemeService.Sha256FileAsync(relAudio) },
            },
        },
    });
    string manifestPath = Path.Combine(e2eDir, "baseuri-test.qxbgm.json");
    await File.WriteAllTextAsync(manifestPath, localJson);
    var m2 = SchemeService.LoadManifestFile(manifestPath);
    var resolved2 = await SchemeService.ResolveAudioAsync(m2, m2.Tracks[0], null, null, default, manifestPath);
    Check(new FileInfo(resolved2).Length == new FileInfo(srcWem).Length,
        "相对音频 URI 经 manifest 基地址端到端落地（字节数一致）");
}
finally { try { Directory.Delete(e2eDir, true); } catch { } }

// P0 真实索引仓库
const string p0Repo = @"D:\ProgramSource2\qxbgm-registry";
if (Directory.Exists(p0Repo))
{
    var (p0, p0Warns) = RegistryService.LoadEntries(p0Repo);
    Check(p0.Any(e => e.Id == "touhou-8bit-youkai-komachi"),
        $"P0 索引仓库读取 {p0.Count} 条（含 touhou-8bit-youkai-komachi，警告 {p0Warns.Count}）");
}
else
{
    Console.WriteLine("SKIP  P0 索引仓库不存在，跳过该断言");
}

// ---- 9. 共享配置中心化（registry 缓存 → 本地文件 → 种子） ----
string sharedDir = Path.Combine(Path.GetTempPath(), "qxbgm-sharedcfg-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(sharedDir, "config"));
    string localMap = Path.Combine(sharedDir, "track-map.json");
    await File.WriteAllTextAsync(localMap,
        """{"tracks":[{"index":90002,"name":"本地曲目","fileID":555002}]}""");

    // 优先级 1：registry 共享缓存（连显式路径也让位）
    AppState.SetSharedConfigs(new Dictionary<uint, TrackInfo>
    {
        [555001] = new TrackInfo { FileId = 555001, Name = "共享曲目", Index = 90001, Category = 101 },
    }, null);
    var viaShared = TrackMapService.Load(localMap);
    Check(viaShared.ContainsKey(555001) && !viaShared.ContainsKey(555002) && !viaShared.ContainsKey(122217526),
        "优先级1：registry 共享缓存覆盖本地文件与种子");
    AppState.SetSharedConfigs(null, null);
    // 优先级 2/3
    var viaLocal = TrackMapService.Load(localMap);
    Check(viaLocal.ContainsKey(555002) && viaLocal.ContainsKey(122217526),
        "优先级2：无共享缓存时读本地文件，种子兜底");
    var viaSeed = TrackMapService.Load(Path.Combine(sharedDir, "no-such.json"));
    Check(viaSeed.Count == 2 && viaSeed.ContainsKey(122217526), "优先级3：文件缺失降级为仅种子");

    // app-config 解析：全字段
    await File.WriteAllTextAsync(Path.Combine(sharedDir, "config", "app-config.json"), """
    {"kind":"app-config","minAppVersion":"1.0.0",
     "encoder":{"sampleRate":48000,"channels":2,"beam":64},
     "presets":[{"id":"normal","name":"普通","lowpassHz":0},{"id":"lp8k","name":"低通8k","lowpassHz":8000,"loop":true}],
     "bookmarks":[{"title":"共享书签","url":"https://example.com"}],
     "knownGameRoots":["C:\\Games"],"audio":{"bytesPerSecond":54000}}
    """);
    var cfg = RegistryService.LoadAppConfig(sharedDir);
    Check(cfg is { Presets.Count: 2 } && cfg.Presets[1].LowpassHz == 8000 && cfg.Presets[1].Loop
          && cfg.Bookmarks.Count == 1 && cfg.Bookmarks[0].Title == "共享书签"
          && cfg.KnownGameRoots.Count == 1 && cfg.Encoder is { Beam: 64, SampleRate: 48000 },
        "app-config 全字段解析（presets/bookmarks/knownGameRoots/encoder）");
    // 缺字段回退
    await File.WriteAllTextAsync(Path.Combine(sharedDir, "config", "app-config.json"),
        """{"kind":"app-config","presets":[{"id":"p1"},{"name":"无名"}]}""");
    var cfgMin = RegistryService.LoadAppConfig(sharedDir);
    Check(cfgMin != null && cfgMin.Presets.Count == 2 && cfgMin.Presets[0].LowpassHz == 0 && !cfgMin.Presets[0].Loop
          && cfgMin.Bookmarks.Count == 0 && cfgMin.Encoder == null && cfgMin.MinAppVersion == "",
        "app-config 缺字段回退（lowpassHz=0/loop=false/空 bookmarks/无 encoder）");
    // 容错：损坏 JSON / kind 不符 → null
    await File.WriteAllTextAsync(Path.Combine(sharedDir, "config", "app-config.json"), "{broken");
    Check(RegistryService.LoadAppConfig(sharedDir) == null, "app-config 损坏 JSON 容错为 null");
    await File.WriteAllTextAsync(Path.Combine(sharedDir, "config", "app-config.json"), """{"kind":"other"}""");
    Check(RegistryService.LoadAppConfig(sharedDir) == null, "app-config kind 不符忽略");
    // LoadSharedTrackMap
    await File.WriteAllTextAsync(Path.Combine(sharedDir, "config", "track-map.json"),
        """{"tracks":[{"index":90002,"name":"本地曲目","fileID":555002}]}""");
    var sm = RegistryService.LoadSharedTrackMap(sharedDir);
    Check(sm is { Count: 1 } && sm.ContainsKey(555002), "LoadSharedTrackMap 读取索引仓库 config/track-map.json");

    // PresetService：共享优先 + 回退
    AppState.SetSharedConfigs(null, new AppConfig { Presets = { new EncoderPreset { Id = "lp4k", Name = "低通4k", LowpassHz = 4000 } } });
    Check(PresetService.GetPresets().Count == 1 && PresetService.HzForPreset("lp4k") == 4000
          && PresetService.HzForPreset("chiptune-6k") == 6000,
        "PresetService 共享预设优先；未知 id 回退硬编码 chiptune-6k=6000Hz");
    AppState.SetSharedConfigs(null, null);
    Check(PresetService.GetPresets().Count == 2 && PresetService.HzForPreset("normal") == 0
          && PresetService.HzForPreset("chiptune-6k") == 6000,
        "PresetService 无共享配置回退硬编码两项");

    // BookmarkService：共享优先；无共享时默认合并仍在
    AppState.SetSharedConfigs(null, new AppConfig { Bookmarks = { new BrowserBookmark { Title = "共享书签B", Url = "https://shared.example" } } });
    Check(BookmarkService.Load().Any(b => b.Url == "https://shared.example"),
        "BookmarkService 共享书签优先");
    AppState.SetSharedConfigs(null, null);
    Check(BookmarkService.Load().Any(b => b.Url == "https://act.mihoyo.com/ys/ugc/tutorial"),
        "BookmarkService 无共享时回退硬编码默认书签");
}
finally
{
    AppState.SetSharedConfigs(null, null);
    try { Directory.Delete(sharedDir, true); } catch { }
}

// ---- 10. 自动更新：版本解析 / 更新判定 / 自更新脚本 ----
Check(UpdateService.ParseVersion("v1.2.0") == "1.2.0"
      && UpdateService.ParseVersion("1.2.0") == "1.2.0"
      && UpdateService.ParseVersion("v1.2.0-beta") == "1.2.0"
      && UpdateService.ParseVersion("release-2.0.1") == "2.0.1"
      && UpdateService.ParseVersion("V10.20.30") == "10.20.30",
    "版本解析：v 前缀/后缀/杂文本前缀容错");
Check(UpdateService.ParseVersion("") == null && UpdateService.ParseVersion(null) == null
      && UpdateService.ParseVersion("latest") == null && UpdateService.ParseVersion("v.x.y") == null,
    "版本解析：无法解析返回 null");
Check(RegistryService.CompareVersions("1.2.0", UpdateService.ParseVersion("v1.2.0")!) == 0,
    "ParseVersion 输出与 CompareVersions 兼容");
// 更新判定：当前 1.1.0
Check(RegistryService.CompareVersions("1.2.0", "1.1.0") > 0, "当前 1.1.0 vs latest 1.2.0 → 有更新");
Check(RegistryService.CompareVersions("1.1.0", "1.1.0") == 0, "当前 1.1.0 vs latest 1.1.0 → 无更新");
Check(RegistryService.CompareVersions("1.0.9", "1.1.0") < 0, "当前 1.1.0 vs latest 1.0.9 → 无更新");

// 自更新脚本内容
const int testPid = 4242;
string testExe = @"D:\app\QxBgmManager.exe";
string testNew = @"C:\Temp\QxBgmManager_new.exe";
var script = UpdateService.BuildUpdateScript(testPid, testExe, testNew);
Check(script.Contains($"PID eq {testPid}") && script.Contains("goto wait"),
    "更新脚本：包含 PID 等待循环");
Check(script.Contains($"copy /y \"{testExe}\" \"{testExe}.bak\""),
    "更新脚本：包含旧 exe 备份（.bak）");
Check(script.Contains($"copy /y \"{testNew}\" \"{testExe}\""),
    "更新脚本：包含新 exe 替换");
Check(script.Contains($"start \"\" \"{testExe}\""),
    "更新脚本：包含重启 exe");
Check(script.Contains("del /q \"%~f0\""),
    "更新脚本：包含自删");
Check(script.Contains(testPid.ToString()) && script.Contains(testNew),
    "更新脚本：PID 与新 exe 路径注入正确");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures;

static string FindWorkspaceRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "tools", "ptadpcm_enc.py")))
            return dir.FullName;
    return @"D:\ProgramSource2\th01_BeyondLocal";
}

// 按规范手工造一个 .qxbgmzip（manifest.json + audio/），与 SchemeService.ExportSchemeZip 产物格式一致
static async Task CreateZipManually(string zipPath, SchemeManifest manifest, string wemPath)
{
    using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
    var entry = zip.CreateEntry("manifest.json");
    await using (var writer = new StreamWriter(entry.Open()))
        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    var audio = zip.CreateEntry("audio/normal.mp3");
    await using (var output = audio.Open())
    {
        var bytes = File.ReadAllBytes(wemPath);
        output.Write(bytes, 0, bytes.Length);
    }
}
