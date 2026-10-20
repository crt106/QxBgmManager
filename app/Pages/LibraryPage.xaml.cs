using System.Windows.Controls;
using System.Windows.Input;
using QxBgmManager.Services;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class LibraryPage : Page
{
    public LibraryPage()
    {
        DataContext = new LibraryViewModel();
        InitializeComponent();
        // 导航缓存模式下回到本页：只有曲库被补丁/复原失效、游戏根变化或尚未加载时才重刷，
        // 避免每次切回都全量重建 188 行导致卡顿
        Loaded += (_, _) =>
        {
            if (DataContext is not LibraryViewModel { IsLoading: false } vm) return;
            bool stale = !AppState.Loaded
                         || AppState.LoadedRoot != SettingsService.Current.GameRoot
                         || vm.AllRowsCount == 0;
            if (stale)
                vm.RefreshCommand.Execute(null);
            else
                vm.SyncPlaybackState();
        };
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 行双击 = 试听
        if (sender is DataGrid { SelectedItem: TrackRow row } &&
            DataContext is LibraryViewModel vm)
        {
            vm.PlayCommand.Execute(row);
        }
    }
}
