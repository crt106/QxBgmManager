using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QxBgmManager;
using QxBgmManager.Pages;
using QxBgmManager.Services;
using QxBgmManager.ViewModels;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

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
                for (int i = 0; i < 30; i++) replace.Jobs.Add(new ReplaceJob { SourcePath = $"测试音频{i}.wav" });
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
