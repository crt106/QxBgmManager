using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using QxBgmManager.Controls;
using QxBgmManager;
using QxBgmManager.Pages;
using QxBgmManager.Services;
using QxBgmManager.ViewModels;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;

internal static class Program
{
    private static int _failures;
    private static readonly Dictionary<string, object> Results = new();
    private static string OutputDirectory
    {
        get
        {
            var dir = Path.Combine(SettingsService.FindWorkspaceRoot(), "build", "verification");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    private static void Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + message);
        if (!ok) _failures++;
    }

    [STAThread]
    public static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        SettingsService.Current = SettingsService.Load();
        if (!SettingsService.IsValidGameRoot(SettingsService.Current.GameRoot))
        {
            Console.WriteLine("SKIP 未配置游戏路径，跳过真实曲库窗口检查");
            return 0;
        }
        // 不联网、不改设置，不调用替换；仅使用真实曲库的只读索引和本地合成列表。
        SettingsService.Current.RegistrySource = "";
        App.ApplyTheme(true);
        var window = new MainWindow { Width = 1280, Height = 520 };
        window.Show();
        window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(window); }
            catch (Exception ex) { Check(false, ex.ToString()); }
            finally
            {
                Results["failures"] = _failures;
                var output = Path.Combine(OutputDirectory, "ui-tests.json");
                File.WriteAllText(output, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }), DispatcherPriority.ApplicationIdle);
        Dispatcher.Run();
        return _failures;
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    private static async Task Run(MainWindow window)
    {
        var navigation = (NavigationView)window.FindName("RootNavigation");
        navigation.Navigate(typeof(LibraryPage));
        await Settle();
        var page = Children<LibraryPage>(window).Single();
        var vm = (LibraryViewModel)page.DataContext;
        for (int i = 0; i < 100 && vm.IsLoading; i++) await Task.Delay(50);
        await Settle();
        var presenter = Children<NavigationViewContentPresenter>(window).Single();
        var grid = Children<DataGrid>(page).Single();
        Check(!presenter.IsDynamicScrollViewerEnabled, "页面禁用导航层额外滚动容器");
        int Realized() => Enumerable.Range(0, grid.Items.Count).Count(i => grid.ItemContainerGenerator.ContainerFromIndex(i) != null);
        int realized = Realized();
        Results["library_rows"] = grid.Items.Count;
        Results["realized_rows"] = realized;
        Check(grid.ActualHeight > 50 && realized < grid.Items.Count / 2, $"188 条曲库仅创建 {realized} 个可见及缓存行，高 {grid.ActualHeight:F0}");
        Snapshot(window, "library");

        // 恢复旧导航布局，量化无限高度对虚拟化的影响，再回到修复后的真实设置。
        presenter.SetCurrentValue(NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty, true);
        await Settle();
        Results["old_layout_realized_rows"] = Realized();
        Results["old_layout_grid_height"] = grid.ActualHeight;
        presenter.SetCurrentValue(NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty, false);
        await Settle();

        var categoryTimings = new List<double>();
        var view = vm.Rows;
        for (int i = 0; i < 8; i++)
        {
            var watch = Stopwatch.StartNew();
            vm.SelectedCategory = vm.Categories[i % 2];
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            categoryTimings.Add(watch.Elapsed.TotalMilliseconds);
        }
        Check(ReferenceEquals(view, vm.Rows), "分类切换复用同一个集合视图");
        Results["category_layout_ms"] = categoryTimings;
        Check(categoryTimings.Max() < 1000, $"分类切换含布局最大 {categoryTimings.Max():F1} ms");
        var navTimings = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            navigation.Navigate(typeof(ConvertPage));
            await Settle();
            var watch = Stopwatch.StartNew();
            navigation.Navigate(typeof(LibraryPage));
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            navTimings.Add(watch.Elapsed.TotalMilliseconds);
        }
        Results["return_library_layout_ms"] = navTimings;
        Check(navTimings.Max() < 1000, $"返回曲库含布局最大 {navTimings.Max():F1} ms");

        foreach (var type in new[] { typeof(ReplaceCenterPage), typeof(ConvertPage), typeof(EnvCheckPage), typeof(SettingsPage), typeof(SchemesPage), typeof(MarketPage) })
        {
            navigation.Navigate(type);
            await Settle();
            var current = Children<Page>(window).Single(p => p.GetType() == type);
            var outer = Children<ScrollViewer>(current).First();
            Snapshot(window, type.Name + "-top");
            Check(outer.ViewportHeight > 0 && outer.ViewportHeight < window.ActualHeight,
                $"{type.Name} 在矮窗口中获得有限滚动视口 {outer.ViewportHeight:F0}");
            if (current.DataContext is ReplaceCenterViewModel replace)
            {
                await CheckTrackPicker(window, current, replace, outer);
                for (int i = 0; i < 30; i++) replace.Jobs.Add(new ReplaceJob
                    { SourcePath = $"测试音频{i}.wav", SourceDurationSeconds = 2, SourceSizeBytes = 192044, IsReadingMetadata = false });
                await Settle();
                var list = Children<ListBox>(current).First(l => ReferenceEquals(l.ItemsSource, replace.Jobs));
                var inner = Children<ScrollViewer>(list).First();
                outer.ScrollToTop(); inner.ScrollToTop();
                await Settle();
                Wheel(inner);
                await Settle();
                Check(inner.VerticalOffset > 0, "替换列表响应滚轮");
                inner.ScrollToBottom();
                await Settle();
                Wheel(inner);
                await Settle();
                Check(outer.VerticalOffset > 0, "内层列表到底后滚轮继续滚动页面");
            }
            if (outer.ScrollableHeight > 0)
            {
                outer.ScrollToTop();
                await Settle();
                Wheel(outer);
                await Settle();
                Check(outer.VerticalOffset > 0, $"{type.Name} 页面响应滚轮");
                outer.ScrollToBottom();
                await Settle();
                Check(Math.Abs(outer.VerticalOffset - outer.ScrollableHeight) < 1, $"{type.Name} 底部可达");
                Snapshot(window, type.Name + "-bottom");
            }
            if (current.DataContext is ConvertViewModel convert)
                await CheckProgress(window, convert, outer);
        }

        // 浏览页离线测量，避免初始化 WebView2 或访问远端网页。
        var browse = new WonderBrowsePage();
        browse.Measure(new Size(1071, 471));
        browse.Arrange(new Rect(0, 0, 1071, 471));
        browse.UpdateLayout();
        foreach (var browser in Children<Microsoft.Web.WebView2.Wpf.WebView2>(browse)) browser.Visibility = Visibility.Collapsed;
        var browserVm = (WonderBrowseViewModel)browse.DataContext;
        for (int i = 0; i < 20; i++) browserVm.Wonders.Add(new QxBgmManager.Models.WonderEntry { Name = "测试奇域" });
        var archive = Children<CardExpander>(browse).Single();
        archive.IsExpanded = true;
        browse.Measure(new Size(1071, 471));
        browse.Arrange(new Rect(0, 0, 1071, 471));
        browse.UpdateLayout();
        var bottom = archive.TranslatePoint(new Point(0, archive.ActualHeight), browse).Y;
        Check(bottom <= 471, $"奇域档案展开后底部仍在矮窗口内（{bottom:F0}）");
        await CheckTaskFooter(window, navigation);
    }

    private static async Task CheckTaskFooter(MainWindow window, NavigationView navigation)
    {
        bool canceled = false;
        using (var activity = TaskActivityService.Begin("合成测试任务", () => canceled = true))
        {
            activity.Report("正在编码 · 42%", 42);
            navigation.Navigate(typeof(ReplaceCenterPage));
            await Settle();
            var footer = (FrameworkElement)window.FindName("TaskFooter");
            var page = Children<ReplaceCenterPage>(window).Single();
            Children<ScrollViewer>(page).First().ScrollToBottom();
            await Settle();
            var position = footer.TranslatePoint(new Point(0, footer.ActualHeight), window);
            Check(footer.IsVisible && position.Y <= window.ActualHeight && footer.ActualHeight > 20,
                "矮窗口滚动到底后固定进度栏仍完整可见");
            navigation.Navigate(typeof(LibraryPage));
            await Settle();
            Check(footer.IsVisible && TaskActivityService.Current.Progress == 42, "切换页面保留全局任务进度");
            Snapshot(window, "workflow-fixed-progress");
            TaskActivityService.Current.CancelCommand.Execute(null);
            Check(canceled, "固定进度栏可取消正在执行的任务");
        }
        Check(!TaskActivityService.Current.IsRunning, "任务结束后禁用全局取消按钮");
        var library = (LibraryViewModel)Children<LibraryPage>(window).Single().DataContext;
        navigation.Navigate(typeof(ConvertPage));
        await Settle();
        long oldRevision = library.LoadedRevision;
        AppState.InvalidateLibrary();
        await AppState.EnsureLibraryAsync(SettingsService.Current.GameRoot);
        navigation.Navigate(typeof(LibraryPage));
        for (int i = 0; i < 100 && (library.IsLoading || library.LoadedRevision == oldRevision); i++) await Task.Delay(30);
        Check(library.LoadedRevision == AppState.Revision && library.LoadedRevision != oldRevision,
            "共享缓存已被其他页面重载后，曲库仍能识别版本变化并刷新");
        navigation.Navigate(typeof(ConvertPage));
        await Settle();
        var convert = (ConvertViewModel)Children<ConvertPage>(window).Single().DataContext;
        oldRevision = convert.LoadedRevision;
        AppState.InvalidateLibrary();
        for (int i = 0; i < 100 && convert.LoadedRevision == oldRevision; i++) await Task.Delay(30);
        Check(convert.LoadedRevision == AppState.Revision, "后台替换发生变化时，当前转换页面自动同步曲库");
        navigation.Navigate(typeof(ReplaceCenterPage));
        await Settle();
        var replace = (ReplaceCenterViewModel)Children<ReplaceCenterPage>(window).Single().DataContext;
        while (replace.RefreshPatchesCommand.IsRunning) await Task.Delay(30);
        var previousTargets = replace.Targets;
        AppState.InvalidateLibrary();
        for (int i = 0; i < 100 && ReferenceEquals(previousTargets, replace.Targets); i++) await Task.Delay(30);
        Check(!ReferenceEquals(previousTargets, replace.Targets), "后台方案发生变化时，当前替换页面自动同步原曲状态");
    }

    private static async Task CheckTrackPicker(Window window, Page page, ReplaceCenterViewModel vm, ScrollViewer outer)
    {
        string dir = Path.Combine(Path.GetTempPath(), "qxbgm-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        double previousHeight = window.Height;
        try
        {
            string path = Path.Combine(dir, "自定义音乐.wav");
            const int samples = 48000 * 3;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + samples * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
                writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(samples * 4); writer.Write(new byte[samples * 4]);
            }
            window.Height = 800;
            vm.AddFiles([path]);
            var job = vm.Jobs.Single();
            for (int i = 0; i < 200 && job.IsReadingMetadata; i++) await Task.Delay(50);
            outer.ScrollToTop();
            await Settle();
            Check(job.SourceDurationSeconds is { } duration && Math.Abs(duration - 3) < 0.001,
                "添加源音频后自动读取真实时长");
            Check(Children<TextBlock>(page).Any(t => t.Text == job.SourceSummary && t.IsVisible)
                  && job.SourceSummary.Contains("0:03") && job.SourceSummary.Contains("源文件"), "任务界面显示源时长、大小和预计转码大小");
            var picker = Children<TrackPicker>(page).Single();
            var toggle = (ToggleButton)picker.FindName("PickerButton");
            Check(toggle.ActualWidth >= picker.ActualWidth - 90, "原曲选择按钮适应可用宽度并为试听按钮预留空间");
            var popup = (Popup)picker.FindName("PickerPopup");
            var search = (System.Windows.Controls.TextBox)picker.FindName("SearchBox");
            var results = (ListBox)picker.FindName("ResultsList");
            var target = vm.Targets.First(t => !t.IsPatched && t.CapacityBytes >= job.EstimatedWemBytes);
            toggle.IsChecked = true;
            await Settle();
            Check(popup.IsOpen && ReferenceEquals(search.DataContext, picker.Model) && results.Items.Count > 0,
                "打开真实原曲下拉后显示搜索、筛选和候选列表");
            var categoryBox = (ListBox)picker.FindName("CategoryFilter");
            var category = picker.Model.Categories.Single(c => c.Key == target.CategoryKey);
            categoryBox.SetCurrentValue(Selector.SelectedItemProperty, category);
            categoryBox.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            await Settle();
            Check(popup.IsOpen && picker.Model.Category == category && results.Items.Cast<TrackRow>().All(r => r.CategoryKey == category.Key),
                "真实分类选项可操作，筛选时原曲面板保持打开");
            picker.Model.ResetCommand.Execute(null);
            search.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, target.FileId.ToString());
            search.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            await Settle();
            Check(results.Items.Count == 1 && ReferenceEquals(results.Items[0], target), "输入原曲编号实时过滤下拉结果");
            SnapshotElement((FrameworkElement)popup.Child, "track-picker-open");
            search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search)!, 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Settle();
            Check(!popup.IsOpen && ReferenceEquals(job.Target, target), "在唯一搜索结果上按回车可选中原曲并关闭下拉");
            Check(Children<TextBlock>(page).Any(t => t.Text == job.TargetSummary && t.IsVisible)
                  && job.TargetSummary.Contains("原曲") && job.TargetSummary.Contains("可用"), "下拉关闭后仍显示所选原曲时长和容量");
            Snapshot(window, "replace-source-and-target-info");
            var alternate = vm.Targets.First(t => !t.IsPatched && t.FileId != target.FileId);
            toggle.IsChecked = true;
            await Settle();
            Check(popup.IsOpen, "选中原曲后可以再次打开下拉改选");
            search.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, alternate.FileId.ToString());
            search.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            await Settle();
            var candidate = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
            candidate.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            await Settle();
            Check(!popup.IsOpen && ReferenceEquals(job.Target, alternate), "鼠标点击候选曲目可完成选择");
            target = alternate;
            window.Height = 520;
            await Settle();
            outer.ScrollToVerticalOffset(200);
            await Settle();
            toggle.IsChecked = true;
            await Settle();
            picker.Model.SearchText = "没有匹配的原曲";
            await Settle();
            Check(results.Items.Count == 0 && ReferenceEquals(job.Target, target), "矮窗口调整搜索结果不会清空已经选好的原曲");
            picker.Model.ResetCommand.Execute(null);
            picker.Model.AvailabilityIndex = 1;
            await Settle();
            Check(results.Items.Count == vm.Targets.Count, "真实下拉可切换全部状态");
            picker.Model.RequiredBytes = long.MaxValue;
            picker.Model.FitsOnly = true;
            await Settle();
            Check(results.Items.Count == 0 && ReferenceEquals(job.Target, target), "真实下拉容量筛选即时生效并保留已选项");
            toggle.IsChecked = false;
            job.Status = "完成";
            await vm.RefreshPatchesCommand.ExecuteAsync(null);
            await Settle();
            Check(job.Target?.FileId == target.FileId && job.Status == "完成", "刷新替换记录后仍保留原曲选择和已完成状态");
            Check(!job.CanRun, "已完成任务禁用单条执行，避免重复替换");
            await CheckPreview(picker);
            vm.IsExecuting = true;
            vm.RemoveJobCommand.Execute(job);
            Check(vm.Jobs.Contains(job), "运行中的任务不能被删除");
            vm.IsExecuting = false;
            var delete = Children<System.Windows.Controls.Button>(page).First(b => b.Content?.ToString() == "删除" && ReferenceEquals(b.CommandParameter, job));
            delete.Command!.Execute(delete.CommandParameter);
            Check(!vm.Jobs.Contains(job), "单条删除按钮只移除指定任务");
            await CheckReplacementExecution(vm, path);
        }
        finally
        {
            vm.ClearJobsCommand.Execute(null);
            window.Height = previousHeight;
            Directory.Delete(dir, true);
            await Settle();
        }
    }

    private static async Task CheckReplacementExecution(ReplaceCenterViewModel vm, string source)
    {
        using var fixture = new SyntheticPckFixture();
        var rows = PckService.ListEntries(fixture.PckPath).Select(e => new TrackRow(e, null, false)).ToList();
        var first = new ReplaceJob { SourcePath = source, Target = rows[0] };
        vm.Jobs.Add(first);
        await vm.RetryJobCommand.ExecuteAsync(first);
        Check(vm.ExecuteStatus.Contains("需要调整") && !PckService.IsPatched(fixture.PckPath) && File.ReadAllBytes(fixture.PckPath).SequenceEqual(fixture.Original),
            "单条重试在容量不足时先拦截，合成音乐包保持原样");
        string shortAudio = Path.Combine(fixture.DirectoryPath, "短音乐.wav");
        const int samples = 4800;
        using (var writer = new BinaryWriter(File.Create(shortAudio)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples * 4); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
            writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples * 4); writer.Write(new byte[samples * 4]);
        }
        first.SourcePath = shortAudio; first.Target = rows[0];
        var second = new ReplaceJob { SourcePath = shortAudio, Target = rows[1] };
        vm.Jobs.Add(second);
        first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReplaceJob.Status) && first.Status == "完成") vm.CancelExecuteCommand.Execute(null); };
        await vm.RunJobsAsync([first, second]);
        Check(!vm.IsExecuting && vm.ExecuteStatus.StartsWith("已取消") && vm.DoneCount == 1 && PckService.ReadBackup(fixture.PckPath)?.Entries.Count == 1,
            "真实替换完成第一首后取消，保留已写入结果且停止后续任务");
        PckService.RestoreAll(fixture.PckPath);
        Check(File.ReadAllBytes(fixture.PckPath).SequenceEqual(fixture.Original), "窗口任务写入的合成音乐包可以逐字节恢复");
        vm.ClearJobsCommand.Execute(null);
    }

    private static async Task CheckPreview(TrackPicker previewPicker)
    {
        if (!File.Exists(SettingsService.ResolvePython()) || !File.Exists(SettingsService.ResolveFfmpeg()))
        { Console.WriteLine("SKIP 未配置音频依赖，跳过窗口试听检查"); return; }
        using var audio = new TestAudioFixture();
        using var pck = new SyntheticPckFixture(File.ReadAllBytes(audio.WemPath));
        var row = new TrackRow(PckService.GetEntry(pck.PckPath, 123), null, false);
        // 使用实际任务行，临时绑定合成静音曲目；始终保留主窗口的输入焦点上下文。
        var previousItems = previewPicker.ItemsSource;
        var previousTrack = previewPicker.SelectedTrack;
        previewPicker.SetCurrentValue(TrackPicker.ItemsSourceProperty, new[] { row });
        previewPicker.SetCurrentValue(TrackPicker.SelectedTrackProperty, row);
        previewPicker.Model.ResetCommand.Execute(null);
        try
        {
            var previewToggle = (ToggleButton)previewPicker.FindName("PickerButton");
            var previewPopup = (Popup)previewPicker.FindName("PickerPopup");
            previewToggle.IsChecked = true;
            await Settle();
            var button = Children<System.Windows.Controls.Button>(previewPopup.Child).First(b => ReferenceEquals(b.Tag, row));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (int i = 0; i < 200 && row.Busy; i++) await Task.Delay(50);
            await Settle();
            Check(previewPopup.IsOpen && ReferenceEquals(previewPicker.SelectedTrack, row), "点击候选试听不会关闭下拉或改动已选原曲");
            Check(previewPicker.Model.PreviewStatus.StartsWith("正在试听") && File.Exists(TrackPreviewService.CachePath(row.Entry, true, ".wav")),
                "选曲试听完成真实合成 WEM 解码并调用播放器");
            SnapshotElement((FrameworkElement)previewPopup.Child, "workflow-picker-preview");
        }
        finally
        {
            TrackPreviewService.Stop();
            ((ToggleButton)previewPicker.FindName("PickerButton")).IsChecked = false;
            previewPicker.SetCurrentValue(TrackPicker.ItemsSourceProperty, previousItems);
            previewPicker.SetCurrentValue(TrackPicker.SelectedTrackProperty, previousTrack);
        }
    }

    private static void SnapshotElement(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(OutputDirectory, name + ".png"));
        encoder.Save(output);
    }

    private static async Task CheckProgress(Window window, ConvertViewModel convert, ScrollViewer outer)
    {
        if (!File.Exists(SettingsService.ResolvePython()) || !File.Exists(SettingsService.ResolveFfmpeg()))
        {
            Console.WriteLine("SKIP 未配置 Python/FFmpeg，跳过真实页面转码检查");
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "qxbgm-ui-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "进度测试.wav");
            const int frames = 48000 * 5;
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
                writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(frames * 4);
                for (int i = 0; i < frames; i++)
                {
                    short value = (short)(20000 * Math.Sin(i * 440 * Math.PI * 2 / 48000));
                    writer.Write(value); writer.Write(value);
                }
            }
            convert.EncodeOutDir = dir;
            convert.AddAudioFiles(new[] { file });
            convert.EnqueueEncodesCommand.Execute(null);
            outer.ScrollToBottom();
            for (int i = 0; i < 150 && convert.QueueProgress <= 2; i++) await Task.Delay(50);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var job = convert.AudioJobs.Single();
            Check(convert.QueueProgress > 2 && convert.QueueProgress < 100 && job.Detail.Contains("预计还需"),
                $"真实转换页面显示中间进度和预计剩余时间：{job.Detail}");
            Snapshot(window, "convert-progress");
            convert.CancelQueueCommand.Execute(null);
            for (int i = 0; i < 100 && convert.IsRunning; i++) await Task.Delay(50);
            Check(!convert.IsRunning && job.Status == "已取消", "转换页面取消后正确结束队列");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Wheel(UIElement source) => source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent });

    private static void Snapshot(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(OutputDirectory, name + ".png"));
        encoder.Save(output);
    }
}
