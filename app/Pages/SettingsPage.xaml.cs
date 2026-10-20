using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        var vm = new SettingsViewModel();
        DataContext = vm;
        InitializeComponent();
        vm.OpenEnvCheckRequested = () =>
        {
            if (Window.GetWindow(this) is MainWindow main)
                main.NavigateTo(typeof(EnvCheckPage));
        };
    }
}
