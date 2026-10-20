using System.Collections.ObjectModel;
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
