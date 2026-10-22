using System.IO;
using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;
using QxBgmManager.Services;

namespace QxBgmManager.Pages;

public partial class ReplaceCenterPage : Page
{
    public ReplaceCenterPage()
    {
        DataContext = new ReplaceCenterViewModel();
        InitializeComponent();
        Loaded += (_, _) => AppState.LibraryChanged += RefreshChanged;
        Unloaded += (_, _) => AppState.LibraryChanged -= RefreshChanged;
        // 补丁状态可能已被方案管理页改动，切回时刷新
        Loaded += (_, _) =>
        {
            if (DataContext is ReplaceCenterViewModel { IsExecuting: false } vm)
                vm.RefreshPatchesCommand.Execute(null);
        };
    }

    private void RefreshChanged()
    {
        if (IsVisible && DataContext is ReplaceCenterViewModel { IsExecuting: false } vm && !vm.RefreshPatchesCommand.IsRunning)
            vm.RefreshPatchesCommand.Execute(null);
    }

    private void JobsList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not ReplaceCenterViewModel vm || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            vm.AddFiles(files.Where(File.Exists));
    }
}
