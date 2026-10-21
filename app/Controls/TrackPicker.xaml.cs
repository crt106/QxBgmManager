using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Controls;

public partial class TrackPicker : UserControl
{
    public TrackPickerViewModel Model { get; } = new();
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<TrackRow>), typeof(TrackPicker), new PropertyMetadata(null, ItemsChanged));
    public static readonly DependencyProperty SelectedTrackProperty = DependencyProperty.Register(
        nameof(SelectedTrack), typeof(TrackRow), typeof(TrackPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((TrackPicker)d).Model.CurrentSelection = (TrackRow?)e.NewValue));
    public static readonly DependencyProperty RequiredBytesProperty = DependencyProperty.Register(
        nameof(RequiredBytes), typeof(long?), typeof(TrackPicker),
        new PropertyMetadata(null, (d, e) => ((TrackPicker)d).Model.RequiredBytes = (long?)e.NewValue));

    public IEnumerable<TrackRow>? ItemsSource { get => (IEnumerable<TrackRow>?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public TrackRow? SelectedTrack { get => (TrackRow?)GetValue(SelectedTrackProperty); set => SetValue(SelectedTrackProperty, value); }
    public long? RequiredBytes { get => (long?)GetValue(RequiredBytesProperty); set => SetValue(RequiredBytesProperty, value); }

    public TrackPicker() => InitializeComponent();

    private static void ItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TrackPicker)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= picker.ItemsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += picker.ItemsCollectionChanged;
        picker.Model.SetItems(picker.ItemsSource);
    }

    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Model.SetItems(ItemsSource);

    private void Popup_Opened(object? sender, EventArgs e)
    {
        ResultsList.SelectedItem = Model.Results.Contains(SelectedTrack) ? SelectedTrack : null;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ResultsList, source) is ListBoxItem { DataContext: TrackRow row }) Commit(row);
    }

    private void Popup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { PickerButton.IsChecked = false; e.Handled = true; PickerButton.Focus(); }
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { PickerButton.IsChecked = false; e.Handled = true; }
        else if (e.Key == Key.Down && Model.Results.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            ResultsList.Focus();
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Model.Results.Count == 1)
        {
            Commit((TrackRow)Model.Results.GetItemAt(0));
            e.Handled = true;
        }
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsList.SelectedItem is TrackRow row) { Commit(row); e.Handled = true; }
        else if (e.Key == Key.Escape) { PickerButton.IsChecked = false; e.Handled = true; }
    }

    private void Commit(TrackRow row)
    {
        SetCurrentValue(SelectedTrackProperty, row);
        PickerButton.IsChecked = false;
        PickerButton.Focus();
    }
}
