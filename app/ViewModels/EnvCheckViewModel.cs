using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

public partial class EnvCheckViewModel : ObservableObject
{
    [ObservableProperty] private bool _isChecking = true;
    [ObservableProperty] private bool _canEnter;
    [ObservableProperty] private string _summaryText = "正在检查环境…";

    public ObservableCollection<EnvCheckItem> Items { get; } = new(EnvCheckService.CreateItems());

    /// <summary>页面注入：导航到指定页面（进入主菜单 / 去设置）。</summary>
    public Action<Type>? NavigateHandler { get; set; }

    private CancellationTokenSource? _cts;

    public EnvCheckViewModel()
    {
        _ = RunAllAsync();
    }

    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        IsChecking = true;
        CanEnter = false;
        SummaryText = "正在检查环境…";
        try
        {
            await EnvCheckService.RunAllAsync(Items, _ => { }, _cts.Token);
            int pass = Items.Count(i => i.Status == EnvCheckStatus.Pass);
            int warn = Items.Count(i => i.Status == EnvCheckStatus.Warn);
            int fail = Items.Count(i => i.Status == EnvCheckStatus.Fail);
            CanEnter = EnvCheckService.AllCriticalPassed(Items);
            SummaryText = CanEnter
                ? $"全部通过（✓{pass}" + (warn > 0 ? $" / ⚠{warn}" : "") + "），可进入主菜单"
                : $"有 {fail} 项关键检查未通过，请修复后重新检查";
        }
        catch (OperationCanceledException)
        {
            SummaryText = "检查已取消。";
        }
        finally
        {
            IsChecking = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void EnterMain() => NavigateHandler?.Invoke(typeof(Pages.LibraryPage));

    [RelayCommand]
    private void OpenSettings() => NavigateHandler?.Invoke(typeof(Pages.SettingsPage));

    /// <summary>修复按钮：按检查项的修复动作分发。</summary>
    [RelayCommand]
    private void FixItem(EnvCheckItem? item)
    {
        if (item?.Fix == EnvCheckFix.OpenSettings)
            OpenSettings();
    }
}
