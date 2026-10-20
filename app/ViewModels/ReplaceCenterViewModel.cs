using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Models;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

/// <summary>
/// 替换中心：单条/批量替换任务（编码→容量校验→patch）与活动补丁生命周期管理合一。
/// </summary>
public partial class ReplaceCenterViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<ReplaceJob> _jobs = new();
    [ObservableProperty] private ObservableCollection<TrackRow> _targets = new();

    [ObservableProperty] private ObservableCollection<PatchRow> _patches = new();
    [ObservableProperty] private bool _hasPatches;
    [ObservableProperty] private bool _gameRunning;
    [ObservableProperty] private string _patchesStatus = "";

    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _executeStatus = "";

    public ObservableCollection<string> LogLines { get; } = new();

    private CancellationTokenSource? _cts;
    private readonly EncodeService _encoder = new();
    private readonly DispatcherTimer _gameWatch;

    public ReplaceCenterViewModel()
    {
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameWatch.Tick += (_, _) => GameRunning = GameProcessService.IsGameRunning();
        _gameWatch.Start();
        GameRunning = GameProcessService.IsGameRunning();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadTargetsAsync();
        await RefreshPatchesAsync();
    }

    // ---- 目标条目 ----

    private async Task LoadTargetsAsync()
    {
        var (ok, message) = await AppState.EnsureLibraryAsync(SettingsService.Current.GameRoot);
        if (!ok)
        {
            ExecuteStatus = message;
            return;
        }
        var rows = AppState.Library
            .Select(e => new TrackRow(e, AppState.InfoOf(e.FileId),
                                      AppState.PatchedIds(e.PckPath).Contains(e.FileId)))
            .OrderByDescending(r => r.Entry.Size)
            .ToList();
        Targets = new ObservableCollection<TrackRow>(rows);
    }

    // ---- 任务列表 ----

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择源音频（可多选，单条替换只加一行）",
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Jobs.Any(j => j.SourcePath == p)) continue;
            Jobs.Add(new ReplaceJob { SourcePath = p });
        }
    }

    [RelayCommand]
    private void ClearJobs() => Jobs.Clear();

    // ---- 批量执行 ----

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsExecuting) return;
        var runnable = Jobs.Where(j => j.Target != null).ToList();
        if (runnable.Count == 0)
        {
            ExecuteStatus = "请为至少一行选择目标条目。";
            return;
        }
        if (GameRunning)
        {
            Log("检测到 YuanShen.exe 正在运行：热替换立即生效，但 pck 可能被占用，写入失败时请关闭游戏重试。");
        }

        _cts = new CancellationTokenSource();
        IsExecuting = true;
        DoneCount = 0;
        TotalCount = runnable.Count;
        ExecuteStatus = "";
        int done = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var job in runnable)
            {
                _cts.Token.ThrowIfCancellationRequested();
                if (job.TargetConflict)
                {
                    job.Status = "跳过";
                    job.Detail = "目标已补丁，需先复原";
                    skipped++;
                }
                else
                {
                    try
                    {
                        await ProcessJobAsync(job, _cts.Token);
                        done++;
                    }
                    catch (IOException ex)
                    {
                        job.Status = "失败";
                        job.Detail = "写入失败：pck 可能被游戏占用，关闭游戏后重试";
                        Log($"{job.FileName}：{ex.Message}");
                        failed++;
                    }
                    catch (Exception ex)
                    {
                        job.Status = "失败";
                        job.Detail = ex.Message;
                        failed++;
                    }
                }
                DoneCount++;
            }
            ExecuteStatus = $"执行完成：成功 {done}，跳过 {skipped}，失败 {failed}。";
            AppState.InvalidateLibrary();
            await LoadTargetsAsync();
            await RefreshPatchesAsync();
        }
        catch (OperationCanceledException)
        {
            ExecuteStatus = $"已取消（完成 {DoneCount}/{runnable.Count}）。已写入的补丁保持有效。";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private async Task ProcessJobAsync(ReplaceJob job, CancellationToken ct)
    {
        var target = job.Target!;
        job.Status = "编码…";
        job.Detail = "";
        int rc = await _encoder.EncodeAsync(job.SourcePath, job.WemPath, job.PresetIndex == 1,
            Log, ct);
        if (rc != 0 || !File.Exists(job.WemPath))
            throw new InvalidOperationException("编码失败（退出码 " + rc + "）");

        job.Status = "容量复核…";
        var entry = PckService.GetEntry(target.Entry.PckPath, target.FileId);
        long wemSize = new FileInfo(job.WemPath).Length;
        if (wemSize > entry.Size)
            throw new InvalidOperationException($"产物 {wemSize:N0} B 超出条目容量 {entry.Size:N0} B，请缩短音频或换更长的目标");

        job.Status = "写入补丁…";
        await Task.Run(() => PckService.Patch(entry.PckPath, entry.FileId, job.WemPath), ct);
        job.Status = "完成";
        job.Detail = $"{wemSize:N0} B ≤ {entry.Size:N0} B";
        Log($"{job.FileName} → {entry.PckName} #{entry.FileId}（{wemSize:N0} B）");
    }

    [RelayCommand]
    private void CancelExecute()
    {
        _cts?.Cancel();
        ExecuteStatus = "正在取消…";
    }

    // ---- 活动补丁 ----

    [RelayCommand]
    private async Task RefreshPatchesAsync()
    {
        var root = SettingsService.Current.GameRoot;
        if (!SettingsService.IsValidGameRoot(root))
        {
            Patches = new ObservableCollection<PatchRow>();
            HasPatches = false;
            PatchesStatus = "游戏根路径无效，请先在设置页配置。";
            return;
        }
        var (ok, message) = await AppState.EnsureLibraryAsync(root);
        if (!ok)
        {
            PatchesStatus = message;
            return;
        }
        var infos = await Task.Run(() => AppState.ScanPatches(root));
        var rows = infos
            .Select(i => new PatchRow(i, TrackMapService.NameOf(AppState.TrackMap, i.FileId)))
            .OrderBy(r => r.Info.PckName).ThenBy(r => r.Info.FileId)
            .ToList();
        Patches = new ObservableCollection<PatchRow>(rows);
        HasPatches = rows.Count > 0;
        PatchesStatus = HasPatches
            ? $"{rows.Count} 条活动补丁（{rows.Select(r => r.Info.PckName).Distinct().Count()} 个 pck）"
            : "当前没有活动补丁，曲库处于原始状态。";
    }

    [RelayCommand]
    private async Task RestoreAsync(PatchRow? row)
    {
        if (row == null || row.Busy) return;
        row.Busy = true;
        try
        {
            await Task.Run(() => PckService.RestoreOne(row.Info.PckPath, row.Info.FileId));
            PatchesStatus = $"已复原 {row.Info.PckName} · {row.TrackName}（fileID {row.Info.FileId}）。";
            AppState.InvalidateLibrary();
            await RefreshPatchesAsync();
        }
        catch (IOException ex)
        {
            PatchesStatus = "复原失败：pck 文件可能被游戏进程占用，请关闭游戏后重试。" + Environment.NewLine + ex.Message;
        }
        catch (Exception ex)
        {
            PatchesStatus = "复原失败：" + ex.Message;
        }
        finally
        {
            row.Busy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (!HasPatches) return;
        var answer = MessageBox.Show(
            $"将复原全部 {Patches.Count} 条补丁，被替换的曲目恢复为原始音乐。继续？",
            "全部复原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            foreach (var pck in Patches.Select(r => r.Info.PckPath).Distinct())
                await Task.Run(() => PckService.RestoreAll(pck));
            PatchesStatus = "全部补丁已复原，曲库恢复原始状态。";
            AppState.InvalidateLibrary();
            await RefreshPatchesAsync();
        }
        catch (IOException ex)
        {
            PatchesStatus = "复原失败：pck 文件可能被游戏进程占用，请关闭游戏后重试。" + Environment.NewLine + ex.Message;
            await RefreshPatchesAsync();
        }
        catch (Exception ex)
        {
            PatchesStatus = "复原失败：" + ex.Message;
            await RefreshPatchesAsync();
        }
    }

    private void Log(string line) => App.Dispatch(() => LogLines.Add(line));
}

/// <summary>一行替换任务。</summary>
public partial class ReplaceJob : ObservableObject
{
    public string SourcePath { get; set; } = "";

    [ObservableProperty] private TrackRow? _target;
    [ObservableProperty] private bool _targetConflict;
    [ObservableProperty] private int _presetIndex;   // 0 普通 / 1 8bit(6kHz 低通)
    [ObservableProperty] private string _status = "待执行";
    [ObservableProperty] private string _detail = "";

    public string FileName => Path.GetFileName(SourcePath);
    public string WemPath => Path.Combine(SettingsService.ImportDir,
        Path.GetFileNameWithoutExtension(SourcePath) + ".wem");

    partial void OnTargetChanged(TrackRow? value)
    {
        TargetConflict = value is { IsPatched: true };
        if (TargetConflict) Detail = "目标已补丁，需先在下方复原";
        else if (Detail == "目标已补丁，需先在下方复原") Detail = "";
    }
}

/// <summary>活动补丁行。</summary>
public partial class PatchRow : ObservableObject
{
    public PatchInfo Info { get; }
    public string PckName => Info.PckName;
    public uint FileId => Info.FileId;
    public string TrackName { get; }
    public string SizeText => $"{Info.OriginalSize / 1048576.0:F2} MB（原始条目）";
    public string TimeText => Info.BackupTime.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty] private bool _busy;

    public PatchRow(PatchInfo info, string trackName)
    {
        Info = info;
        TrackName = trackName;
    }
}
