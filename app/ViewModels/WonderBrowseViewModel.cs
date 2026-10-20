using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Models;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

/// <summary>
/// 奇域浏览页：WebView2 内嵌浏览器 + 本地奇域档案。
/// 浏览器实例由页面持有，VM 通过页面注入的委托钩子操作（WebView2 缺失时钩子为空，不崩溃）。
/// </summary>
public partial class WonderBrowseViewModel : ObservableObject
{
    [ObservableProperty] private string _addressText = "";
    [ObservableProperty] private bool _isNavigating;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private string _browserError = "";

    /// <summary>WebView2 初始化失败时的降级提示。</summary>
    public bool HasBrowserError => !string.IsNullOrWhiteSpace(BrowserError);

    partial void OnBrowserErrorChanged(string value)
        => OnPropertyChanged(nameof(HasBrowserError));

    // 奇域档案编辑区
    [ObservableProperty] private WonderEntry? _selectedWonder;
    [ObservableProperty] private string _archiveStatus = "";

    public ObservableCollection<BrowserBookmark> Bookmarks { get; } = new();
    public ObservableCollection<WonderEntry> Wonders { get; } = new();

    /// <summary>页面注入：导航到 URL。</summary>
    public Action<string>? NavigateHandler { get; set; }
    /// <summary>页面注入：刷新当前页。</summary>
    public Action? RefreshHandler { get; set; }
    /// <summary>页面注入：读取当前页 URL。</summary>
    public Func<string?>? CurrentUrlGetter { get; set; }
    /// <summary>页面注入：读取当前页标题。</summary>
    public Func<string?>? CurrentTitleGetter { get; set; }

    public WonderBrowseViewModel()
    {
        foreach (var b in BookmarkService.Load()) Bookmarks.Add(b);
        foreach (var w in WonderArchiveService.Load()) Wonders.Add(w);
    }

    // ---- 导航 ----

    [RelayCommand]
    private void Go()
    {
        var input = AddressText.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            NavigateHandler?.Invoke(uri.ToString());
            return;
        }
        // 关键词 → 必应搜索
        var query = Uri.EscapeDataString(input);
        NavigateHandler?.Invoke($"https://www.bing.com/search?q={query}");
    }

    [RelayCommand]
    private void GoHome() => NavigateHandler?.Invoke("https://act.mihoyo.com/ys/ugc/tutorial");

    [RelayCommand]
    private void GoBack() => NavigateHandler?.Invoke("__back__");
    [RelayCommand]
    private void GoForward() => NavigateHandler?.Invoke("__forward__");

    [RelayCommand]
    private void Refresh() => RefreshHandler?.Invoke();

    /// <summary>页面在 CoreWebView2 导航完成后回写状态。</summary>
    public void OnNavigated(string url, bool canGoBack, bool canGoForward)
    {
        AddressText = url;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        IsNavigating = false;
    }

    public void OnNavigationStarted() => IsNavigating = true;

    public void OnNavigationStopped() => IsNavigating = false;

    // ---- 书签 ----

    [RelayCommand]
    private void OpenBookmark(BrowserBookmark? bookmark)
    {
        if (bookmark != null) NavigateHandler?.Invoke(bookmark.Url);
    }

    [RelayCommand]
    private void AddBookmark()
    {
        var url = CurrentUrlGetter?.Invoke();
        if (string.IsNullOrWhiteSpace(url)) return;
        var title = CurrentTitleGetter?.Invoke();
        if (string.IsNullOrWhiteSpace(title)) title = url;
        if (Bookmarks.Any(b => b.Url == url)) return;
        var bookmark = new BrowserBookmark { Title = title, Url = url };
        Bookmarks.Add(bookmark);
        BookmarkService.Save(Bookmarks);
    }

    [RelayCommand]
    private void RemoveBookmark(BrowserBookmark? bookmark)
    {
        if (bookmark == null) return;
        Bookmarks.Remove(bookmark);
        BookmarkService.Save(Bookmarks);
    }

    // ---- 奇域档案 ----

    [RelayCommand]
    private void AddWonder()
    {
        Wonders.Add(new WonderEntry());
        SelectedWonder = Wonders[^1];
    }

    [RelayCommand]
    private void RemoveWonder(WonderEntry? entry)
    {
        if (entry == null) return;
        Wonders.Remove(entry);
        SaveWonders();
    }

    [RelayCommand]
    private void SaveWonders()
    {
        WonderArchiveService.Save(Wonders);
        ArchiveStatus = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>从当前网页登记：自动填入 URL 与页面标题。</summary>
    [RelayCommand]
    private void RegisterFromWeb()
    {
        var url = CurrentUrlGetter?.Invoke();
        if (string.IsNullOrWhiteSpace(url))
        {
            ArchiveStatus = "浏览器不可用，无法读取当前页。";
            return;
        }
        var title = CurrentTitleGetter?.Invoke();
        var entry = new WonderEntry
        {
            Id = "",
            Name = string.IsNullOrWhiteSpace(title) ? url : title!,
            Url = url,
            Note = "",
            SchemeId = "",
        };
        Wonders.Add(entry);
        SelectedWonder = entry;
        ArchiveStatus = "已根据当前网页新建条目，请补充奇域ID 后保存。";
    }
}
