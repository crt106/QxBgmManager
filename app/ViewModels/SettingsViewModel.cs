using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _gameRoot = "";
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _pythonPath = "";
    [ObservableProperty] private string _registrySource = "";
    [ObservableProperty] private bool _themeDark = true;

    // ---- 自动更新 ----
    [ObservableProperty] private string _appVersionText = "";
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateVersionText = "";
    [ObservableProperty] private string _updateBodyText = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent = -1;
    [ObservableProperty] private string _updateStatus = "未检查";
    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private bool _gameRootOk;
    [ObservableProperty] private string _gameRootStatus = "未校验";

    /// <summary>自动探测到的游戏安装。</summary>
    public ObservableCollection<string> DetectedRoots { get; } = new();

    /// <summary>页面注入：跳转到环境检查页。</summary>
    public Action? OpenEnvCheckRequested { get; set; }

    public SettingsViewModel()
    {
        var s = SettingsService.Current;
        foreach (var root in SettingsService.DetectGameRoots()) DetectedRoots.Add(root);

        GameRoot = s.GameRoot;
        if (!SettingsService.IsValidGameRoot(GameRoot) && DetectedRoots.Count > 0)
            GameRoot = DetectedRoots[0];
        FfmpegPath = string.IsNullOrWhiteSpace(s.FfmpegPath) ? SettingsService.DefaultFfmpegPath : s.FfmpegPath;
        PythonPath = string.IsNullOrWhiteSpace(s.PythonPath) ? "python" : s.PythonPath;
        RegistrySource = string.IsNullOrWhiteSpace(s.RegistrySource)
            ? @"https://github.com/crt106/qxbgm-registry.git"
            : s.RegistrySource;
        ThemeDark = s.ThemeDark;
        AppVersionText = UpdateService.CurrentVersion;
        ValidateGameRoot();
    }

    partial void OnGameRootChanged(string value) => ValidateGameRoot();

    private void ValidateGameRoot()
    {
        if (SettingsService.IsValidGameRoot(GameRoot))
        {
            var pcks = SettingsService.MusicPckPaths(GameRoot).Count;
            GameRootOk = true;
            GameRootStatus = $"有效：找到 BeyondUGC 目录（{pcks}/8 个 BeydMusic pck）";
        }
        else
        {
            GameRootOk = false;
            GameRootStatus = string.IsNullOrWhiteSpace(GameRoot)
                ? "未设置"
                : "无效：未找到 YuanShen_Data\\StreamingAssets\\AudioAssets\\BeyondUGC 目录";
        }
    }

    [RelayCommand]
    private void BrowseGameRoot()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择游戏根目录（含 YuanShen_Data 的那一级）" };
        if (dlg.ShowDialog() == true) GameRoot = dlg.FolderName;
    }

    [RelayCommand]
    private void UseDetectedRoot(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root)) GameRoot = root;
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var p = BrowseExe("选择 ffmpeg.exe");
        if (p != null) FfmpegPath = p;
    }

    [RelayCommand]
    private void BrowsePython()
    {
        var p = BrowseExe("选择 python.exe");
        if (p != null) PythonPath = p;
    }

    private static string? BrowseExe(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "可执行文件|*.exe", Title = title };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    [RelayCommand]
    private void OpenEnvCheck() => OpenEnvCheckRequested?.Invoke();

    // ---- 自动更新 ----

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateAvailable = false;
        UpdateStatus = "检查中…";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (!string.IsNullOrEmpty(info.Error))
            {
                UpdateStatus = "检查失败：" + info.Error;
            }
            else if (info.HasUpdate)
            {
                _pendingUpdate = info;
                UpdateAvailable = true;
                UpdateVersionText = info.Version;
                UpdateBodyText = info.Body.Length > 500 ? info.Body[..500] + "…" : info.Body;
                UpdateStatus = $"发现新版本 v{info.Version}（约 {info.Size / 1048576.0:F1} MB）";
            }
            else
            {
                UpdateStatus = "已是最新版本。";
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndUpdateAsync()
    {
        if (_pendingUpdate == null || IsDownloading) return;
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadPercent = 0;
        UpdateStatus = "下载中…";
        try
        {
            string path = await UpdateService.DownloadAsync(_pendingUpdate,
                new Progress<(long Done, long Total)>(p =>
                    DownloadPercent = p.Total > 0 ? p.Done * 100.0 / p.Total : DownloadPercent),
                _downloadCts.Token);
            IsDownloading = false;
            DownloadPercent = 100;
            var answer = MessageBox.Show("下载完成，将重启完成更新。现在重启？",
                "更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                string? error = UpdateService.ApplyAndRestart(path);
                if (error != null) UpdateStatus = error;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "下载已取消。";
        }
        catch (Exception ex)
        {
            UpdateStatus = "下载失败：" + ex.Message;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void Save()
    {
        var s = SettingsService.Current;
        s.GameRoot = GameRoot;
        s.FfmpegPath = FfmpegPath;
        s.PythonPath = PythonPath;
        s.RegistrySource = RegistrySource;
        s.ThemeDark = ThemeDark;
        SettingsService.Save();
        ValidateGameRoot();
        GameRootStatus += "（已保存）";
    }

    partial void OnThemeDarkChanged(bool value)
    {
        // 主题唯一写入路径：用户显式点击单选（SetThemeCommand）→ 此处应用并保存；
        // 加载/导航/初始化路径均为 OneWay 绑定，不会触达这里。
        App.ApplyTheme(value);
        SettingsService.Current.ThemeDark = value;
        SettingsService.Save();
    }

    [RelayCommand]
    private void SetTheme(string? mode)
    {
        if (mode == "dark") ThemeDark = true;
        else if (mode == "light") ThemeDark = false;
    }
}
