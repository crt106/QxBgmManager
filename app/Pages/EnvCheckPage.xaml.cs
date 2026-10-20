using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class EnvCheckPage : Page
{
    public EnvCheckPage()
    {
        var vm = new EnvCheckViewModel();
        DataContext = vm;
        InitializeComponent();
        vm.NavigateHandler = type =>
        {
            if (Window.GetWindow(this) is MainWindow main)
                main.NavigateTo(type);
        };
        // 每次落到本页自动重跑检查
        Loaded += (_, _) =>
        {
            if (DataContext is EnvCheckViewModel { IsChecking: false } v)
                v.RunAllCommand.Execute(null);
        };
    }
}
