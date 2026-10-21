using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QxBgmManager.ViewModels;

public sealed record TrackCategoryFilter(int Key, string Name);

/// <summary>每个任务独立筛选，筛选结果变化不会清空已选原曲。</summary>
public partial class TrackPickerViewModel : ObservableObject
{
    private readonly List<TrackRow> _items = [];
    public ListCollectionView Results { get; }
    public IReadOnlyList<TrackCategoryFilter> Categories { get; } =
    [new(0, "全部分类"), new(101, "探索音乐"), new(102, "战斗音乐"), new(103, "任务音乐"), new(104, "其他需求"), new(-1, "未识别")];
    public IReadOnlyList<string> AvailabilityOptions { get; } = ["可替换", "全部状态", "已替换"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private TrackCategoryFilter? _category;
    [ObservableProperty] private int _availabilityIndex;
    [ObservableProperty] private bool _fitsOnly;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilterByCapacity))]
    private long? _requiredBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    private TrackRow? _currentSelection;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _hasResults;
    public bool CanFilterByCapacity => RequiredBytes.HasValue;
    public string SelectionText => CurrentSelection?.PickerTitle ?? "搜索并选择原曲…";

    public TrackPickerViewModel()
    {
        _category = Categories[0];
        Results = new ListCollectionView(_items);
        Refresh();
    }

    public void SetItems(IEnumerable<TrackRow>? items)
    {
        _items.Clear();
        if (items != null) _items.AddRange(items);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnCategoryChanged(TrackCategoryFilter? value) => Refresh();
    partial void OnAvailabilityIndexChanged(int value) => Refresh();
    partial void OnFitsOnlyChanged(bool value) => Refresh();
    partial void OnRequiredBytesChanged(long? value) => Refresh();

    private void Refresh()
    {
        var tokens = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Results.Filter = item => item is TrackRow row &&
            (Category is null or { Key: 0 } || row.CategoryKey == Category.Key) &&
            (AvailabilityIndex switch { 0 => !row.IsPatched, 2 => row.IsPatched, _ => true }) &&
            (!FitsOnly || RequiredBytes is not { } required || row.CapacityBytes >= required) &&
            tokens.All(token => $"{row.Info?.Name} {row.FileId} {row.IndexText} {row.Album} {row.PckName}"
                .Contains(token, StringComparison.OrdinalIgnoreCase));
        HasResults = Results.Count > 0;
        CountText = $"{Results.Count} / {_items.Count} 首";
    }

    [RelayCommand]
    private void Reset()
    {
        SearchText = "";
        Category = Categories[0];
        AvailabilityIndex = 0;
        FitsOnly = false;
    }
}
