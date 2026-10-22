using System.IO;
using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;
using QxBgmManager.Services;

namespace QxBgmManager.Pages;

public partial class ConvertPage : Page
{
    public ConvertPage()
    {
        DataContext = new ConvertViewModel();
        InitializeComponent();
        ExtractPicker.Model.AvailabilityIndex = 1;
        Loaded += (_, _) => AppState.LibraryChanged += RefreshChanged;
        Unloaded += (_, _) => AppState.LibraryChanged -= RefreshChanged;
        Loaded += async (_, _) =>
        {
            if (DataContext is ConvertViewModel vm && (!AppState.Loaded || vm.LoadedRevision != AppState.Revision))
                await vm.LoadLibraryAsync();
        };
    }

    private async void RefreshChanged()
    {
        if (IsVisible && DataContext is ConvertViewModel vm) await vm.LoadLibraryAsync();
    }

    private void AudioList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not ConvertViewModel vm || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            vm.AddAudioFiles(files.Where(File.Exists));
    }

    private void WemList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not ConvertViewModel vm || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            vm.AddWemFiles(files.Where(f => f.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)));
    }
}
