using System.Windows;
using Wpf.Ui.Controls;

namespace QxBgmManager;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
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
    }
}
