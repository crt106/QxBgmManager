using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class MarketPage : Page
{
    public MarketPage()
    {
        var vm = new MarketViewModel();
        DataContext = vm;
        InitializeComponent();
        vm.OpenSettingsRequested = () =>
        {
            if (Window.GetWindow(this) is MainWindow main)
                main.NavigateTo(typeof(SettingsPage));
        };
    }
}
