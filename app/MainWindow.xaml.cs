using System.Windows;
using Wpf.Ui.Controls;

namespace QxBgmManager;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // 硬件渲染实验：关闭 Backdrop（Mica/Auto 在部分 NVIDIA 驱动 + 硬件渲染下整窗黑屏）
        if (Services.SettingsService.Current.RenderHardware)
            WindowBackdropType = WindowBackdropType.None;
        Loaded += (_, _) =>
        {
            // 首次启动落地页：环境检查（通过后才进主菜单）
            RootNavigation.Navigate(typeof(Pages.EnvCheckPage));
        };
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.TargetPageType != null)
            sender.Navigate(item.TargetPageType);
    }

    /// <summary>供各页面跳转（进入主菜单 / 去设置等）。</summary>
    public void NavigateTo(Type pageType)
    {
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nav)
                nav.IsActive = nav.TargetPageType == pageType;
        }
        foreach (var item in RootNavigation.FooterMenuItems)
        {
            if (item is NavigationViewItem nav)
                nav.IsActive = nav.TargetPageType == pageType;
        }
        RootNavigation.Navigate(pageType);

        // 首次进入主菜单后后台静默检查更新（有新版出横幅，失败静默）
        if (pageType == typeof(Pages.LibraryPage))
            SilentCheckUpdateAsync();
    }

    private bool _updateChecked;

    private async void SilentCheckUpdateAsync()
    {
        if (_updateChecked) return;
        _updateChecked = true;
        var info = await Services.UpdateService.CheckAsync();
        if (!info.HasUpdate) return;
        Dispatcher.Invoke(() =>
        {
            UpdateBannerVersion.Text = info.Version;
            UpdateBanner.Visibility = Visibility.Visible;
        });
    }

    private void UpdateBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        NavigateTo(typeof(Pages.SettingsPage));
    }
}
