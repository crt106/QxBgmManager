using System.Windows;

namespace QxBgmManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = Services.SettingsService.Load();
        Services.SettingsService.Current = settings;
        // 保存过的代理配置先于任何网络请求生效（无效地址忽略，设置页会给出反馈）
        Services.ProxyConfig.Apply(settings.ProxyUrl);
        ApplyTheme(settings.ThemeDark);
        // 默认硬件渲染（Mica 已在 MainWindow 关闭以规避 NVIDIA 黑屏）；RenderHardware=false 时降级软件渲染兜底
        if (!settings.RenderHardware)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var window = new MainWindow();
        window.Show();
    }

    public static void ApplyTheme(bool dark)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        var mint = System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xB7);
        var mintHover = System.Windows.Media.Color.FromRgb(0x8F, 0xF0, 0xC9);
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(mint, mint, mint, mintHover);
    }

    /// <summary>子进程日志等后台线程回调统一切回 UI 线程。</summary>
    public static void Dispatch(Action action)
    {
        if (Current is { Dispatcher: { } d }) d.Invoke(action);
        else action();
    }
}
