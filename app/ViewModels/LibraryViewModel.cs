using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private const int CategoryAll = 0;
    private const int CategoryUnknown = -1;

    private readonly List<TrackRow> _allRows = new();

    /// <summary>左侧分类栏（全部 / 探索 / 战斗 / 任务 / 其他 / 未识别）。</summary>
    [ObservableProperty] private ObservableCollection<CategoryItem> _categories = new();
    [ObservableProperty] private CategoryItem? _selectedCategory;

    /// <summary>过滤/排序后的可见行。</summary>
    public ListCollectionView Rows { get; }
    private bool _rebuilding;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _patchFilterIndex;   // 0 全部 / 1 已补丁 / 2 未补丁
    [ObservableProperty] private string _pckFilter = "全部";
    [ObservableProperty] private int _sortIndex;          // 0 索引 / 1 时长 / 2 大小 / 3 fileID

    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    public IReadOnlyList<string> PckFilters { get; } = new[] { "全部" }
        .Concat(Enumerable.Range(0, 8).Select(i => $"BeydMusic{i}.pck")).ToList();

    private int _searchSeq;
    private readonly EncodeService _encoder = new();

    /// <summary>全量行数（性能自测用）。</summary>
    internal int AllRowsCount => _allRows.Count;
    internal long LoadedRevision { get; private set; } = -1;

    public LibraryViewModel()
    {
        Rows = new ListCollectionView(_allRows);
        AudioPreviewService.PlaybackChanged += OnPlaybackChanged;
        _ = RefreshAsync();
    }

    // ---- 加载 ----

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var swTotal = Stopwatch.StartNew();
        IsLoading = true;
        StatusText = "正在加载曲库…";
        try
        {
            var root = SettingsService.Current.GameRoot;
            var sw = Stopwatch.StartNew();
            var (ok, message) = await AppState.EnsureLibraryAsync(root);
            sw.Stop();
            Debug.WriteLine($"[Perf] EnsureLibraryAsync: {sw.ElapsedMilliseconds} ms");
            if (!ok)
            {
                _allRows.Clear();
                ApplyFilters();
                StatusText = message;
                return;
            }

            sw.Restart();
            _allRows.Clear();
            var patches = AppState.Library.Select(e => e.PckPath).Distinct()
                .ToDictionary(p => p, AppState.PatchedIds);
            foreach (var e in AppState.Library)
            {
                var patched = patches[e.PckPath];
                _allRows.Add(new TrackRow(e, AppState.InfoOf(e.FileId), patched.Contains(e.FileId)));
            }
            sw.Stop();
            Debug.WriteLine($"[Perf] 构建 {_allRows.Count} 个 TrackRow: {sw.ElapsedMilliseconds} ms");

            RebuildCategories();
            ApplyFilters();
            StatusText = $"已加载 {_allRows.Count} 首音乐";
            OnPlaybackChanged();
            LoadedRevision = AppState.Revision;
            swTotal.Stop();
            Debug.WriteLine($"[Perf] RefreshAsync 总计（数据+VM 侧）: {swTotal.ElapsedMilliseconds} ms；渲染在 UI 线程另行发生");
        }
        catch (Exception ex)
        {
            StatusText = "曲库加载失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildCategories()
    {
        var selectedKey = SelectedCategory?.Key ?? CategoryAll;
        var defs = new (int Key, string Name)[]
        {
            (CategoryAll, "全部"),
            (101, "探索音乐"),
            (102, "战斗音乐"),
            (103, "任务音乐"),
            (104, "其他需求"),
            (CategoryUnknown, "未识别"),
        };
        var items = defs.Select(d => new CategoryItem(d.Key, d.Name)
        {
            Count = d.Key == CategoryAll ? _allRows.Count
                : _allRows.Count(r => r.CategoryKey == d.Key),
        }).ToList();
        _rebuilding = true;
        try
        {
            Categories = new ObservableCollection<CategoryItem>(items);
            SelectedCategory = items.FirstOrDefault(i => i.Key == selectedKey) ?? items[0];
        }
        finally { _rebuilding = false; }
    }

    // ---- 过滤与排序 ----

    partial void OnSearchTextChanged(string value) => _ = DebounceSearchAsync();

    private async Task DebounceSearchAsync()
    {
        int seq = ++_searchSeq;
        await Task.Delay(300);
        if (seq == _searchSeq) ApplyFilters();
    }

    partial void OnSelectedCategoryChanged(CategoryItem? value) => ApplyFilters();
    partial void OnPatchFilterIndexChanged(int value) => ApplyFilters();
    partial void OnPckFilterChanged(string value) => ApplyFilters();
    partial void OnSortIndexChanged(int value) => ApplyFilters();

    internal void ApplyFilters()
    {
        if (_rebuilding) return;
        var sw = Stopwatch.StartNew();
        IEnumerable<TrackRow> q = _allRows;

        if (SelectedCategory != null && SelectedCategory.Key != CategoryAll)
            q = q.Where(r => r.CategoryKey == SelectedCategory.Key);

        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var s = search.ToLowerInvariant();
            q = q.Where(r =>
                (!string.IsNullOrEmpty(r.ChineseName) && r.ChineseName.ToLowerInvariant().Contains(s)) ||
                (!string.IsNullOrEmpty(r.EnglishName) && r.EnglishName.ToLowerInvariant().Contains(s)) ||
                r.FileId.ToString().Contains(s) ||
                (r.IndexText != "—" && r.IndexText.Contains(s)) ||
                (!string.IsNullOrEmpty(r.Album) && r.Album.ToLowerInvariant().Contains(s)) ||
                r.TrackName.ToLowerInvariant().Contains(s));
        }

        q = PatchFilterIndex switch
        {
            1 => q.Where(r => r.IsPatched),
            2 => q.Where(r => !r.IsPatched),
            _ => q,
        };

        if (!string.IsNullOrWhiteSpace(PckFilter) && PckFilter != "全部")
            q = q.Where(r => r.PckName == PckFilter);

        q = SortIndex switch
        {
            1 => q.OrderByDescending(r => r.DurationSeconds),
            2 => q.OrderByDescending(r => r.Entry.Size),
            3 => q.OrderBy(r => r.FileId),
            _ => q.OrderBy(r => r.CategoryKey == CategoryUnknown)   // 未识别排最后
                    .ThenBy(r => r.Info == null || r.Info.Index <= 0
                        ? long.MaxValue
                        : r.Info.Index)
                    .ThenBy(r => r.FileId),
        };

        // 保持同一个视图和行实例，避免每次筛选都替换 ItemsSource、重建整个表格。
        var filtered = q.ToList();
        var visible = filtered.ToHashSet();
        var order = filtered.Select((row, index) => (row, index)).ToDictionary(p => p.row, p => p.index);
        using (Rows.DeferRefresh())
        {
            Rows.Filter = item => item is TrackRow row && visible.Contains(row);
            Rows.CustomSort = Comparer<TrackRow>.Create((a, b) => order.GetValueOrDefault(a, int.MaxValue)
                .CompareTo(order.GetValueOrDefault(b, int.MaxValue)));
        }
        HasRows = Rows.Count > 0;
        var cat = SelectedCategory is { Key: not CategoryAll } c ? $" · {c.Name}" : "";
        StatsText = $"共 {Rows.Count} 条{cat}";
        sw.Stop();
        Debug.WriteLine($"[Perf] ApplyFilters: {sw.ElapsedMilliseconds} ms, {Rows.Count} 行");
    }

    // ---- 试听 ----

    [RelayCommand]
    private async Task PlayAsync(TrackRow? row)
    {
        if (row == null) return;
        if (row.Playing)
        {
            AudioPreviewService.Stop();
            return;
        }
        AudioPreviewService.Stop();
        row.Busy = true;
        try
        {
            StatusText = $"正在准备试听「{row.TrackName}」…";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await TrackPreviewService.PlayAsync(row, false, cts.Token);
            StatusText = $"正在试听「{row.TrackName}」";
        }
        catch (Exception ex)
        {
            StatusText = "试听失败：" + ex.Message;
        }
        finally
        {
            row.Busy = false;
        }
    }

    private void OnPlaybackChanged()
    {
        foreach (var r in _allRows) r.Playing = AudioPreviewService.IsPlayingPath(r.WavPreviewPath);
    }

    /// <summary>切回页面且未重刷时同步播放态（内部/自测用）。</summary>
    internal void SyncPlaybackState() => OnPlaybackChanged();
}

/// <summary>左侧分类栏一项。</summary>
public partial class CategoryItem : ObservableObject
{
    public int Key { get; }
    public string Name { get; }
    [ObservableProperty] private int _count;
    public CategoryItem(int key, string name) { Key = key; Name = name; }
}
