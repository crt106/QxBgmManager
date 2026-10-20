using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using QxBgmManager.Models;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class WonderBrowsePage : Page
{
    private readonly WonderBrowseViewModel _vm;

    public WonderBrowsePage()
    {
        _vm = new WonderBrowseViewModel();
        DataContext = _vm;
        InitializeComponent();
        _vm.NavigateHandler = OnNavigate;
        _vm.RefreshHandler = () =>
        {
            try { Browser?.CoreWebView2?.Reload(); } catch { }
        };
        _vm.CurrentUrlGetter = () =>
        {
            try { return Browser?.CoreWebView2?.Source; } catch { return null; }
        };
        _vm.CurrentTitleGetter = () =>
        {
            try { return Browser?.CoreWebView2?.DocumentTitle; } catch { return null; }
        };
        Loaded += async (_, _) => await InitBrowserAsync();
    }

    private async Task InitBrowserAsync()
    {
        if (Browser == null) return;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.SourceChanged += (_, _) =>
                _vm.OnNavigated(Browser.CoreWebView2.Source,
                    Browser.CoreWebView2.CanGoBack, Browser.CoreWebView2.CanGoForward);
            Browser.CoreWebView2.NavigationStarting += (_, _) => _vm.OnNavigationStarted();
            Browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                    _vm.OnNavigated(Browser.CoreWebView2.Source,
                        Browser.CoreWebView2.CanGoBack, Browser.CoreWebView2.CanGoForward);
                else
                    _vm.OnNavigationStopped();
            };
            // 主页：官方综合指南
            Browser.CoreWebView2.Navigate("https://act.mihoyo.com/ys/ugc/tutorial");
        }
        catch (Exception ex)
        {
            // WebView2 运行时缺失等场景：降级为提示文本，不崩溃
            _vm.BrowserError = ex.Message + "（本功能需要 Microsoft Edge WebView2 运行时）";
        }
    }

    private void OnNavigate(string target)
    {
        try
        {
            if (Browser?.CoreWebView2 == null) return;
            if (target == "__back__") { Browser.CoreWebView2.GoBack(); return; }
            if (target == "__forward__") { Browser.CoreWebView2.GoForward(); return; }
            Browser.CoreWebView2.Navigate(target);
        }
        catch (Exception ex)
        {
            _vm.BrowserError = ex.Message;
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.GoCommand.Execute(null);
            Keyboard.ClearFocus();
        }
    }
}
