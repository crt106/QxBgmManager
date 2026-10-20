using System.Windows.Controls;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class SchemesPage : Page
{
    public SchemesPage()
    {
        DataContext = new SchemesViewModel();
        InitializeComponent();
        // 导航缓存模式下回到本页时刷新已安装列表
        Loaded += (_, _) =>
        {
            if (DataContext is SchemesViewModel vm)
                vm.RefreshInstalledCommand.Execute(null);
        };
    }
}
