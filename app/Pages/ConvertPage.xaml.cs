using System.IO;
using System.Windows;
using System.Windows.Controls;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Pages;

public partial class ConvertPage : Page
{
    public ConvertPage()
    {
        DataContext = new ConvertViewModel();
        InitializeComponent();
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
