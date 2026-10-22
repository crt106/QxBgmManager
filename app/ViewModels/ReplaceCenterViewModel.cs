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
    [ObservableProperty] private double _executeProgress;

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>编码预设显示名（优先索引仓库 app-config.json，回退硬编码两项）。</summary>
    public IReadOnlyList<string> PresetNames { get; } =
        PresetService.GetPresets().Select(p => p.Name).ToList();

    private static double HzForIndex(int index)
    {
        var presets = PresetService.GetPresets();
        return index >= 0 && index < presets.Count ? presets[index].LowpassHz : 0;
    }

    private CancellationTokenSource? _cts;
    private CancellationTokenSource _metadataCts = new();
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
        await RefreshPatchesAsync();
    }

    // ---- 目标条目 ----

    private void UpdateTargets(IReadOnlyList<PatchInfo> patches)
    {
        var backups = patches.ToDictionary(p => (p.PckPath, p.FileId));
        var rows = AppState.Library
            .Select(e => new TrackRow(e, AppState.InfoOf(e.FileId),
                backups.ContainsKey((e.PckPath, e.FileId)), backups.GetValueOrDefault((e.PckPath, e.FileId))?.OriginalSize))
            .OrderByDescending(r => r.CapacityBytes)
            .ToList();
        Targets = new ObservableCollection<TrackRow>(rows);
        foreach (var job in Jobs.Where(j => j.Target != null))
        {
            var previous = job.Target!;
            job.Target = rows.FirstOrDefault(r => r.FileId == previous.FileId &&
                string.Equals(r.Entry.PckPath, previous.Entry.PckPath, StringComparison.OrdinalIgnoreCase));
        }
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
        if (IsExecuting) return;
        foreach (var p in paths)
        {
            if (!File.Exists(p) || Jobs.Any(j => j.SourcePath == p)) continue;
            var job = new ReplaceJob { SourcePath = p };
            Jobs.Add(job);
            _ = ReadSourceInfoAsync(job, _metadataCts.Token);
        }
    }

    private async Task ReadSourceInfoAsync(ReplaceJob job, CancellationToken ct)
    {
        string source = job.SourcePath;
        try
        {
            var metadata = await Task.Run(() => AudioMetadataService.ReadAsync(source, ct), ct);
            if (ct.IsCancellationRequested || !Jobs.Contains(job) || job.SourcePath != source) return;
            job.SourceSizeBytes = metadata.SizeBytes;
            job.SourceDurationSeconds = metadata.DurationSeconds;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"读取音频信息失败：{job.FileName}：{ex.Message}"); }
        finally { if (job.SourcePath == source) job.IsReadingMetadata = false; }
    }

    [RelayCommand]
    private void ClearJobs()
    {
        if (IsExecuting) return;
        _metadataCts.Cancel();
        _metadataCts.Dispose();
        _metadataCts = new();
        Jobs.Clear();
    }

    // ---- 批量执行 ----

    [RelayCommand]
    private Task ExecuteAsync() => RunJobsAsync(Jobs.Where(j => j.Status != "完成").ToList());

    [RelayCommand]
    private Task RetryJobAsync(ReplaceJob? job) => job == null || job.Status == "完成"
        ? Task.CompletedTask : RunJobsAsync([job]);

    [RelayCommand]
    private void RemoveJob(ReplaceJob? job)
    {
        if (!IsExecuting && job != null) Jobs.Remove(job);
    }

    [RelayCommand]
    private void ChangeSource(ReplaceJob? job)
    {
        if (IsExecuting || job == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "更换源音频", Filter = "音频文件|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac|所有文件|*.*" };
        if (dlg.ShowDialog() != true) return;
        job.SourcePath = dlg.FileName;
        job.Status = "待执行"; job.Detail = ""; job.Progress = 0;
        job.SourceDurationSeconds = null; job.IsReadingMetadata = true;
        _ = ReadSourceInfoAsync(job, _metadataCts.Token);
    }

    private TaskActivityService.Activity? _activity;
    private double _completedWeight, _totalWeight = 1;
    private static double Weight(ReplaceJob job) => Math.Max(1, job.SourceDurationSeconds ?? 60);

    internal async Task RunJobsAsync(IReadOnlyList<ReplaceJob> candidates)
    {
        if (IsExecuting) return;
        if (candidates.Count == 0) { ExecuteStatus = "请添加待执行的任务"; return; }
        _cts = new CancellationTokenSource();
        IsExecuting = true;
        DoneCount = 0; ExecuteProgress = 0; TotalCount = candidates.Count;
        _activity = TaskActivityService.Begin("替换音乐", () => _cts?.Cancel());
        int done = 0, failed = 0;
        try
        {
            ExecuteStatus = "正在检查任务…";
            _activity.Report(ExecuteStatus, 0);
            // 重新读取文件头，源文件自添加后被修改时也使用最新时长。
            foreach (var job in candidates)
            {
                _cts.Token.ThrowIfCancellationRequested();
                await ReadSourceInfoAsync(job, _cts.Token);
            }
            var errors = await Task.Run(() => ReplacementPreflight.Check(candidates), _cts.Token);
            if (errors.Count > 0)
            {
                foreach (var (job, error) in errors) { job.Status = "需调整"; job.Detail = error; }
                ExecuteStatus = $"有 {errors.Count} 条任务需要调整，尚未开始替换";
                _activity.Report(ExecuteStatus, 0);
                return;
            }
            _completedWeight = 0; _totalWeight = candidates.Sum(Weight);
            foreach (var job in candidates)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try { await ProcessJobAsync(job, _cts.Token); done++; }
                catch (OperationCanceledException) { job.Status = "已取消"; job.Detail = "可重试继续"; throw; }
                catch (Exception ex) { job.Status = "失败"; job.Detail = ex.Message; Log($"{job.FileName}：{ex.Message}"); failed++; }
                DoneCount++;
                _completedWeight += Weight(job);
                ExecuteProgress = _completedWeight * 100 / _totalWeight;
                _activity.Report($"已处理 {DoneCount}/{TotalCount} 首", ExecuteProgress);
            }
            ExecuteStatus = $"执行完成：成功 {done}，失败 {failed}";
            _activity.Report(ExecuteStatus, 100);
        }
        catch (OperationCanceledException)
        {
            ExecuteStatus = $"已取消，已完成 {done} 首";
            _activity.Report(ExecuteStatus, ExecuteProgress);
        }
        catch (Exception ex) { ExecuteStatus = "任务检查失败：" + ex.Message; _activity.Report(ExecuteStatus, ExecuteProgress); }
        finally
        {
            AppState.InvalidateLibrary();
            try { await RefreshPatchesAsync(); }
            catch (Exception ex) { Log("刷新状态失败：" + ex.Message); }
            IsExecuting = false;
            _activity.Dispose(); _activity = null;
            _cts.Dispose(); _cts = null;
        }
    }

    private async Task ProcessJobAsync(ReplaceJob job, CancellationToken ct)
    {
        var target = job.Target!;
        job.Status = "编码…";
        job.Detail = "";
        int rc = await _encoder.EncodeAsync(job.SourcePath, job.WemPath, HzForIndex(job.PresetIndex),
            Log, ct, onProgress: p => App.Dispatch(() =>
            {
                job.Progress = p.Percent;
                job.Detail = p.DisplayText;
                ExecuteProgress = (_completedWeight + Weight(job) * p.Percent / 100 * 0.95) * 100 / _totalWeight;
                _activity?.Report(job.FileName + " · " + p.DisplayText, ExecuteProgress);
            }));
        if (rc != 0 || !File.Exists(job.WemPath))
            throw new InvalidOperationException("编码失败（退出码 " + rc + "）");

        job.Status = "容量复核…";
        var entry = PckService.GetEntry(target.Entry.PckPath, target.FileId);
        long wemSize = new FileInfo(job.WemPath).Length;
        if (wemSize > entry.Size)
            throw new InvalidOperationException($"产物 {wemSize:N0} B 超出条目容量 {entry.Size:N0} B，请缩短音频或换更长的目标");

        ct.ThrowIfCancellationRequested();
        job.Status = "正在替换…";
        await Task.Run(() => PckService.Patch(entry.PckPath, entry.FileId, job.WemPath), ct);
        job.Status = "完成";
        job.Progress = 100;
        job.Detail = "";
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
            Targets = new ObservableCollection<TrackRow>();
            foreach (var job in Jobs) job.Target = null;
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
        try
        {
        var infos = await Task.Run(() => AppState.ScanPatches(root));
        UpdateTargets(infos);
        var rows = infos
            .Select(i => new PatchRow(i, TrackMapService.NameOf(AppState.TrackMap, i.FileId)))
            .OrderBy(r => r.Info.PckName).ThenBy(r => r.Info.FileId)
            .ToList();
        Patches = new ObservableCollection<PatchRow>(rows);
        HasPatches = rows.Count > 0;
        PatchesStatus = HasPatches
            ? $"已替换 {rows.Count} 首音乐"
            : "暂无替换记录";
        }
        catch (Exception ex) { PatchesStatus = "读取替换记录失败，请检查备份"; Log(ex.Message); }
    }

    [RelayCommand]
    private async Task RestoreAsync(PatchRow? row)
    {
        if (row == null || row.Busy || IsExecuting) return;
        row.Busy = true;
        try
        {
            await Task.Run(() => PckService.RestoreOne(row.Info.PckPath, row.Info.FileId));
            PatchesStatus = $"已恢复「{row.TrackName}」";
            AppState.InvalidateLibrary();
            await RefreshPatchesAsync();
        }
        catch (IOException ex)
        {
            PatchesStatus = "恢复失败，请关闭游戏后重试。";
            Log(ex.Message);
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
        if (!HasPatches || IsExecuting) return;
        var answer = MessageBox.Show(
            $"恢复全部 {Patches.Count} 首原曲？",
            "恢复原曲", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            foreach (var pck in Patches.Select(r => r.Info.PckPath).Distinct())
                await Task.Run(() => PckService.RestoreAll(pck));
            PatchesStatus = "已恢复全部原曲";
            AppState.InvalidateLibrary();
            await RefreshPatchesAsync();
        }
        catch (IOException ex)
        {
            PatchesStatus = "恢复失败，请关闭游戏后重试。";
            Log(ex.Message);
            await RefreshPatchesAsync();
        }
        catch (Exception ex)
        {
            PatchesStatus = "复原失败：" + ex.Message;
            await RefreshPatchesAsync();
        }
    }

    private void Log(string line) => App.Dispatch(() =>
    {
        LogLines.Add(line);
        while (LogLines.Count > 300) LogLines.RemoveAt(0);
    });
}

/// <summary>一行替换任务。</summary>
public partial class ReplaceJob : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    [NotifyPropertyChangedFor(nameof(WemPath))]
    private string _sourcePath = "";
    private readonly string _outputId = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSummary))]
    [NotifyPropertyChangedFor(nameof(CapacityAssessment))]
    [NotifyPropertyChangedFor(nameof(CapacityWarning))]
    private TrackRow? _target;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CapacityWarning))]
    private bool _targetConflict;
    [ObservableProperty] private int _presetIndex;   // 0 普通 / 1 8bit(6kHz 低通)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(RunLabel))]
    [NotifyPropertyChangedFor(nameof(CapacityAssessment))]
    [NotifyPropertyChangedFor(nameof(CapacityWarning))]
    private string _status = "待执行";
    public bool CanRun => Status != "完成";
    public string RunLabel => Status is "失败" or "已取消" or "需调整" or "跳过" ? "重试" : "执行";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSummary))]
    private bool _isReadingMetadata = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSummary))]
    private long _sourceSizeBytes;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceSummary))]
    [NotifyPropertyChangedFor(nameof(EstimatedWemBytes))]
    [NotifyPropertyChangedFor(nameof(CapacityAssessment))]
    [NotifyPropertyChangedFor(nameof(CapacityWarning))]
    private double? _sourceDurationSeconds;

    public long? EstimatedWemBytes => AudioMetadata.EstimateWemBytes(SourceDurationSeconds);
    public string SourceSummary => IsReadingMetadata ? "正在读取音频时长…"
        : (SourceDurationSeconds is { } seconds ? $"时长 {TrackRow.FormatSeconds(seconds)}" : "时长暂不可用")
          + $" · 源文件 {AudioMetadata.FormatBytes(SourceSizeBytes)}"
          + (EstimatedWemBytes is { } size ? $" · 预计转码 {AudioMetadata.FormatBytes(size)}" : "");
    public string TargetSummary => Target is { } row ? row.ReplacementInfoText + $" · 文件编号 {row.FileId}" : "";
    public bool CapacityWarning => Status != "完成" && (TargetConflict || (Target is { } row && EstimatedWemBytes is { } size && size > row.CapacityBytes));
    public string CapacityAssessment => Status == "完成" ? "已替换" : TargetConflict ? "这首原曲已替换，请先在下方恢复"
        : Target is not { } row || EstimatedWemBytes is not { } size ? ""
        : size <= row.CapacityBytes ? "预计容量充足"
        : $"预计超出容量 {AudioMetadata.FormatBytes(size - row.CapacityBytes)}，请选择更长的原曲或缩短音频";

    public string FileName => Path.GetFileName(SourcePath);
    public string WemPath => Path.Combine(SettingsService.ImportDir,
        _outputId + ".wem");

    partial void OnTargetChanged(TrackRow? oldValue, TrackRow? newValue)
    {
        bool sameTrack = oldValue?.FileId == newValue?.FileId &&
            string.Equals(oldValue?.Entry.PckPath, newValue?.Entry.PckPath, StringComparison.OrdinalIgnoreCase);
        if (!sameTrack && Status is "完成" or "跳过" or "失败" or "需调整" or "已取消") { Status = "待执行"; Detail = ""; }
        TargetConflict = newValue is { IsPatched: true };
        if (TargetConflict && Status != "完成") Detail = "请先在下方恢复这首原曲";
        else if (Detail == "请先在下方恢复这首原曲") Detail = "";
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
