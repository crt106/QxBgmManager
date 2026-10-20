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

    [RelayCommand]
    private async Task InstallAsync(MarketEntryRow? row)
    {
        if (row == null || row.Busy) return;
        row.Busy = true;
        row.StateText = "安装中…";
        row.Detail = "";
        try
        {
            var (manifest, packageZip, baseUri) = await ObtainManifestAsync(row.Entry);
            var (ok, message) = await AppState.EnsureLibraryAsync(SettingsService.Current.GameRoot);
            if (!ok) throw new SchemeException(message);

            var installed = new InstalledScheme
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Author = manifest.Author,
                Description = manifest.Description,
                InstalledAt = DateTime.Now,
                RegistryId = row.Entry.Id,
                RegistryVersion = row.Entry.Version,
            };
            int patched = 0, skipped = 0;
            foreach (var t in manifest.Tracks)
            {
                var entry = SchemeService.FindTarget(t, AppState.Library)
                            ?? throw new SchemeException($"目标条目不在曲库：fileID {t.Target.FileId}");
                var r = await SchemeService.InstallTrackAsync(manifest, t, entry, packageZip, baseUri,
                    _encoder, Log, CancellationToken.None);
                if (r != null) { installed.Tracks.Add(r); patched++; }
                else skipped++;
            }
            InstallRegistryService.Upsert(installed);
            AppState.InvalidateLibrary();
            row.Installed = installed;
            row.StateText = $"已安装 v{row.Entry.Version}";
            row.Detail = $"打入 {patched} 条" + (skipped > 0 ? $"，跳过已补丁 {skipped} 条" : "");
            if (manifest.Wonderland != null)
                WonderPrompt = $"本方案关联奇域「{manifest.Wonderland.Name}」（{manifest.Wonderland.Id}），可一键登记到奇域档案。";
            _lastInstalled = manifest;
            Log($"市场安装完成：{row.Entry.Name} v{row.Entry.Version}（{patched} 条）");
        }
        catch (Exception ex)
        {
            row.StateText = row.Installed != null
                ? $"已安装 v{row.Installed.RegistryVersion}"
                : "未安装";
            row.Detail = "安装失败：" + ex.Message;
            Log($"{row.Entry.Name} 安装失败：{ex.Message}");
        }
        finally
        {
            row.Busy = false;
        }
    }

    /// <summary>获取清单：优先整包 package（.qxbgmzip），否则下载 manifest 并按其来源确定基地址。</summary>
    private async Task<(SchemeManifest Manifest, string? PackageZip, string? BaseUri)> ObtainManifestAsync(RegistryEntry entry)
    {
        var marketDir = Path.Combine(SettingsService.ResearchDir, "market");
        if (entry.Package != null && !string.IsNullOrWhiteSpace(entry.Package.Url))
        {
            string dest = Path.Combine(marketDir, $"{entry.Id}@{entry.Version}.qxbgmzip");
            await RegistryService.FetchFileAsync(entry.Package.Url, _repoDir, dest,
                entry.Package.Sha256, null, CancellationToken.None);
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
            entry.Manifest.Sha256, null, CancellationToken.None);
        return (SchemeService.LoadManifestFile(tmp), null, url);
    }

    [RelayCommand]
    private async Task UpgradeAsync(MarketEntryRow? row)
    {
        if (row == null) return;
        var ok = await UninstallAsync(row);
        if (ok) await InstallAsync(row);
    }

    [RelayCommand]
    private async Task<bool> UninstallAsync(MarketEntryRow? row)
    {
        if (row == null || row.Busy) return false;
        row.Busy = true;
        try
        {
            var installed = row.Installed
                            ?? InstallRegistryService.Load().FirstOrDefault(s => s.RegistryId == row.Entry.Id);
            if (installed == null)
            {
                row.StateText = "未安装";
                return true;
            }
            int restored = 0;
            var errors = new List<string>();
            foreach (var t in installed.Tracks)
            {
                try
                {
                    await Task.Run(() => PckService.RestoreOne(t.PckPath, t.FileId));
                    restored++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{t.PckName} #{t.FileId}：{ex.Message}");
                }
            }
            InstallRegistryService.Remove(installed.Id);
            AppState.InvalidateLibrary();
            row.Installed = null;
            row.StateText = "未安装";
            row.Detail = $"已复原 {restored}/{installed.Tracks.Count} 条" +
                         (errors.Count > 0 ? "；" + string.Join("；", errors.Take(2)) : "");
            return errors.Count == 0;
        }
        finally
        {
            row.Busy = false;
        }
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

    public bool IsInstalled => StateText.StartsWith("已安装", StringComparison.Ordinal);
    public bool IsNotInstalled => !IsInstalled && !Busy;
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
            : RegistryService.IsUpgradeAvailable(installed.RegistryVersion, entry.Version)
                ? $"可升级（已装 v{installed.RegistryVersion} → v{entry.Version}）"
                : $"已安装 v{installed.RegistryVersion}";
    }
}
