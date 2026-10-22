using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Models;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

/// <summary>
/// 方案市场：索引源同步（本地路径/git URL 浅克隆）→ 条目浏览/搜索/分类筛选 →
/// 一键安装（package 整包或 manifest + 基地址解析音频）→ 版本升级提示。
/// </summary>
public partial class MarketViewModel : ObservableObject
{
    private readonly List<MarketEntryRow> _allRows = new();

    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _statusText = "尚未加载索引。";
    [ObservableProperty] private string _warningsText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ObservableCollection<MarketEntryRow> _rows = new();
    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private string _selectedCategory = "全部";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _wonderPrompt = "";

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>索引仓库目录（同步后确定，用于解析相对路径资产）。</summary>
    private string _repoDir = "";
    private SchemeManifest? _lastInstalled;
    private readonly EncodeService _encoder = new();
    private int _searchSeq;

    /// <summary>页面注入：跳转到设置页（索引源配置）。</summary>
    public Action? OpenSettingsRequested { get; set; }

    public MarketViewModel()
    {
        SourceText = SettingsService.Current.RegistrySource;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    // ---- 索引同步与加载 ----

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在同步索引源…";
        try
        {
            var source = SettingsService.Current.RegistrySource;
            var (dir, message) = await RegistryService.SyncAsync(source, CancellationToken.None);
            _repoDir = dir;

            // 共享配置：config/track-map.json + config/app-config.json → AppState 缓存
            var sharedMap = RegistryService.LoadSharedTrackMap(dir);
            var sharedConfig = RegistryService.LoadAppConfig(dir);
            AppState.SetSharedConfigs(sharedMap, sharedConfig);
            AppState.InvalidateLibrary();

            var (entries, warnings) = await Task.Run(() => RegistryService.LoadEntries(dir));
            WarningsText = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : "";

            var installed = InstallRegistryService.Load()
                .Where(s => !string.IsNullOrWhiteSpace(s.RegistryId))
                .ToDictionary(s => s.RegistryId!, s => s);
            _allRows.Clear();
            foreach (var e in entries)
                _allRows.Add(new MarketEntryRow(e, installed.GetValueOrDefault(e.Id)));
            RebuildCategories();
            ApplyFilters();
            StatusText = $"{message}；共 {entries.Count} 个条目" +
                         (sharedMap != null ? $"；共享曲名映射 {sharedMap.Count} 条" : "") +
                         (sharedConfig != null ? "；共享应用配置已加载" : "") +
                         (warnings.Count > 0 ? $"（{warnings.Count} 条损坏已跳过）" : "");
        }
        catch (Exception ex)
        {
            StatusText = "加载索引失败：" + ex.Message;
            Rows = new ObservableCollection<MarketEntryRow>();
            _allRows.Clear();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildCategories()
    {
        var cats = new[] { "全部" }.Concat(_allRows.Select(r => r.Entry.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c)).ToList();
        Categories = new ObservableCollection<string>(cats);
        SelectedCategory = cats.Contains(SelectedCategory) ? SelectedCategory : "全部";
    }

    partial void OnSearchTextChanged(string value) => _ = DebounceSearchAsync();

    private async Task DebounceSearchAsync()
    {
        int seq = ++_searchSeq;
        await Task.Delay(300);
        if (seq == _searchSeq) ApplyFilters();
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        IEnumerable<MarketEntryRow> q = _allRows;
        if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != "全部")
            q = q.Where(r => r.Entry.Category == SelectedCategory);
        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var s = search.ToLowerInvariant();
            q = q.Where(r =>
                r.Entry.Name.ToLowerInvariant().Contains(s) ||
                r.Entry.Author.ToLowerInvariant().Contains(s) ||
                r.Entry.Description.ToLowerInvariant().Contains(s) ||
                r.Entry.Tags.Any(t => t.ToLowerInvariant().Contains(s)));
        }
        Rows = new ObservableCollection<MarketEntryRow>(q);
    }

    // ---- 安装 / 升级 / 卸载 ----

    private CancellationTokenSource? _installCts;

    [RelayCommand]
    private Task InstallAsync(MarketEntryRow? row) => InstallOrUpgradeAsync(row, false);

    private async Task InstallOrUpgradeAsync(MarketEntryRow? row, bool upgrading)
    {
        if (row == null || IsBusy || row.Busy) return;
        IsBusy = true;
        row.Busy = true;
        _installCts = new CancellationTokenSource();
        using var activity = TaskActivityService.Begin(upgrading ? "升级方案" : "安装方案", () => _installCts?.Cancel());
        row.StateText = "准备中…";
        try
        {
            var ct = _installCts.Token;
            var (manifest, packageZip, baseUri) = await ObtainManifestAsync(row.Entry, ct);
            var (ok, message) = await AppState.EnsureLibraryAsync(SettingsService.Current.GameRoot);
            if (!ok) throw new SchemeException(message);
            var previous = InstallRegistryService.Load().FirstOrDefault(s => s.RegistryId == row.Entry.Id || s.Id == manifest.Id);
            using var prepared = await SchemeInstallService.PrepareAsync(manifest, manifest.Tracks, AppState.Library,
                packageZip, baseUri, previous, upgrading, Log, (i, text, p) => App.Dispatch(() =>
                {
                    row.Detail = text;
                    activity.Report(text, 80.0 * (i + (p?.Percent ?? 0) / 100) / manifest.Tracks.Count);
                }), ct);
            if (upgrading && previous != null && !await SchemeInstallService.UninstallAsync(previous, ct))
                throw new SchemeException("部分原曲尚未恢复，请重试恢复后升级");
            var installed = new InstalledScheme
            {
                Id = manifest.Id, Name = manifest.Name, Author = manifest.Author, Description = manifest.Description,
                InstalledAt = DateTime.Now, RegistryId = row.Entry.Id, RegistryVersion = row.Entry.Version,
            };
            await SchemeInstallService.InstallAsync(installed, prepared, (i, text) => App.Dispatch(() =>
            {
                row.Detail = text;
                activity.Report(text, 80 + 20.0 * (i + 1) / manifest.Tracks.Count);
            }), ct);
            row.Detail = $"已安装 {installed.Tracks.Count} 首音乐";
            activity.Report(row.Detail, 100);
            _lastInstalled = manifest;
            if (manifest.Wonderland != null) WonderPrompt = $"可登记奇域「{manifest.Wonderland.Name}」";
        }
        catch (Exception ex)
        {
            row.Detail = ex is OperationCanceledException ? "已取消，已完成的曲目可在方案管理中恢复" : "安装未完成：" + ex.Message;
            activity.Report(row.Detail, 0);
            Log(row.Detail);
        }
        finally
        {
            AppState.InvalidateLibrary();
            RefreshInstalledRow(row);
            row.Busy = false;
            IsBusy = false;
            _installCts.Dispose();
            _installCts = null;
        }
    }

    /// <summary>获取清单：优先整包 package（.qxbgmzip），否则下载 manifest 并按其来源确定基地址。</summary>
    private async Task<(SchemeManifest Manifest, string? PackageZip, string? BaseUri)> ObtainManifestAsync(RegistryEntry entry, CancellationToken ct)
    {
        var marketDir = Path.Combine(SettingsService.ResearchDir, "market");
        if (entry.Package != null && !string.IsNullOrWhiteSpace(entry.Package.Url))
        {
            string dest = Path.Combine(marketDir, $"{entry.Id}@{entry.Version}.qxbgmzip");
            await RegistryService.FetchFileAsync(entry.Package.Url, _repoDir, dest,
                entry.Package.Sha256, null, ct);
            return (SchemeService.LoadManifestFile(dest), dest, null);
        }
        if (entry.Manifest == null || string.IsNullOrWhiteSpace(entry.Manifest.Url))
            throw new SchemeException("条目缺少 manifest.url 与 package.url");
        string url = entry.Manifest.Url;
        if (!RegistryService.IsGitUrl(url) && !Path.IsPathRooted(url) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 相对索引仓库的 manifest 路径
            url = Path.GetFullPath(Path.Combine(_repoDir, url));
        }
        string tmp = Path.Combine(marketDir, $"{entry.Id}@{entry.Version}.qxbgm.json");
        await RegistryService.FetchFileAsync(url, _repoDir, tmp,
            entry.Manifest.Sha256, null, ct);
        return (SchemeService.LoadManifestFile(tmp), null, url);
    }

    [RelayCommand]
    private Task UpgradeAsync(MarketEntryRow? row) => InstallOrUpgradeAsync(row, true);

    [RelayCommand]
    private async Task<bool> UninstallAsync(MarketEntryRow? row)
    {
        if (row == null || row.Busy || IsBusy) return false;
        IsBusy = true;
        row.Busy = true;
        using var cts = new CancellationTokenSource();
        using var activity = TaskActivityService.Begin("恢复方案", cts.Cancel);
        try
        {
            var installed = InstallRegistryService.Load().FirstOrDefault(s => s.RegistryId == row.Entry.Id);
            bool ok = installed == null || await SchemeInstallService.UninstallAsync(installed, cts.Token);
            row.Detail = ok ? "已恢复全部原曲" : "部分恢复失败，可再次点击恢复";
            activity.Report(row.Detail, ok ? 100 : 0);
            return ok;
        }
        catch (Exception ex) { row.Detail = ex is OperationCanceledException ? "恢复已取消" : ex.Message; activity.Report(row.Detail, 0); return false; }
        finally
        {
            RefreshInstalledRow(row);
            row.Busy = false;
            IsBusy = false;
        }
    }

    private static void RefreshInstalledRow(MarketEntryRow row)
    {
        try
        {
            row.Installed = InstallRegistryService.Load().FirstOrDefault(s => s.RegistryId == row.Entry.Id);
            row.StateText = row.Installed == null ? "未安装" : row.Installed.Status == "已安装"
                ? $"已安装 v{row.Installed.RegistryVersion}" : row.Installed.Status;
        }
        catch (Exception ex) { row.StateText = "登记读取失败"; row.Detail = ex.Message; }
    }

    [RelayCommand]
    private void RegisterWonder()
    {
        if (_lastInstalled?.Wonderland == null) return;
        var list = WonderArchiveService.Load();
        if (!list.Any(w => w.Id == _lastInstalled.Wonderland.Id))
        {
            list.Add(new WonderEntry
            {
                Id = _lastInstalled.Wonderland.Id,
                Name = _lastInstalled.Wonderland.Name,
                Url = _lastInstalled.Wonderland.Url,
                Note = "由方案市场安装登记",
                SchemeId = _lastInstalled.Id,
            });
            WonderArchiveService.Save(list);
        }
        WonderPrompt = "";
        StatusText = "已登记到奇域档案（奇域浏览页可查看）。";
    }

    private void Log(string line) => App.Dispatch(() =>
    {
        LogLines.Add(line);
        while (LogLines.Count > 300) LogLines.RemoveAt(0);
    });
}

/// <summary>市场条目行。</summary>
public partial class MarketEntryRow : ObservableObject
{
    public RegistryEntry Entry { get; }
    public InstalledScheme? Installed { get; set; }

    public string Name => Entry.Name;
    public string Author => string.IsNullOrWhiteSpace(Entry.Author) ? "未知作者" : Entry.Author;
    public string VersionText => "v" + Entry.Version;
    public string CategoryText => string.IsNullOrWhiteSpace(Entry.Category) ? "未分类" : Entry.Category;
    public string TagsText => Entry.Tags.Count > 0 ? string.Join(" · ", Entry.Tags) : "";
    public string TracksText => Entry.Tracks > 0 ? $"{Entry.Tracks} 首" : "";
    public string UpdatedText => Entry.UpdatedAt;
    public string WonderlandText => Entry.Wonderland != null ? $"奇域：{Entry.Wonderland.Name}" : "";
    public string Description => Entry.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(CanUpgrade))]
    private string _stateText;

    [ObservableProperty] private string _detail = "";

    public bool IsInstalled => Installed != null;
    public bool IsNotInstalled => (!IsInstalled || Installed?.Status == "安装未完成") && !Busy;
    public bool CanUpgrade => StateText.StartsWith("可升级", StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(CanUpgrade))]
    private bool _busy;

    public MarketEntryRow(RegistryEntry entry, InstalledScheme? installed)
    {
        Entry = entry;
        Installed = installed;
        _stateText = installed == null
            ? "未安装"
            : installed.Status != "已安装" ? installed.Status
            : RegistryService.IsUpgradeAvailable(installed.RegistryVersion, entry.Version)
                ? $"可升级（已装 v{installed.RegistryVersion} → v{entry.Version}）"
                : $"已安装 v{installed.RegistryVersion}";
    }
}
