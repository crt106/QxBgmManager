using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Models;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

/// <summary>
/// 编码转换页：音频→wem、wem→wav/mp3、pck 条目→wem 三个独立分组，共享一个顺序执行队列（并发 1）。
/// </summary>
public partial class ConvertViewModel : ObservableObject
{
    public const string JobEncode = "音频→wem";
    public const string JobDecode = "wem→音频";
    public const string JobExtract = "pck→wem";

    // 输入列表
    [ObservableProperty] private ObservableCollection<ConvertJob> _audioJobs = new();
    [ObservableProperty] private ObservableCollection<ConvertJob> _wemJobs = new();

    // 输出目录（各分组独立）
    [ObservableProperty] private string _encodeOutDir = "";
    [ObservableProperty] private string _decodeOutDir = "";
    [ObservableProperty] private string _extractOutDir = "";

    // wem→音频格式：0=wav 1=mp3
    [ObservableProperty] private int _decodeFormatIndex;

    // pck 抽取
    [ObservableProperty] private string _extractFileIdText = "";
    [ObservableProperty] private ObservableCollection<TrackRow> _libraryEntries = new();
    [ObservableProperty] private TrackRow? _selectedLibraryEntry;

    // 队列状态
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _queueDone;
    [ObservableProperty] private int _queueTotal;
    [ObservableProperty] private string _queueStatus = "";

    public ObservableCollection<ConvertJob> Queue { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private CancellationTokenSource? _cts;
    private readonly EncodeService _runner = new();

    public ConvertViewModel()
    {
        var def = Path.Combine(SettingsService.ResearchDir, "out");
        EncodeOutDir = def;
        DecodeOutDir = def;
        ExtractOutDir = def;
        _ = LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        var (ok, _) = await AppState.EnsureLibraryAsync(SettingsService.Current.GameRoot);
        if (!ok) return;
        var rows = AppState.Library
            .Select(e => new TrackRow(e, AppState.InfoOf(e.FileId),
                                      AppState.PatchedIds(e.PckPath).Contains(e.FileId)))
            .OrderBy(r => r.PckName).ThenBy(r => r.FileId)
            .ToList();
        LibraryEntries = new ObservableCollection<TrackRow>(rows);
    }

    // ---- 文件添加（浏览 + 拖放共用）----

    [RelayCommand]
    private void AddAudioFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择源音频（可多选）",
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddAudioFiles(dlg.FileNames);
    }

    public void AddAudioFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (AudioJobs.Any(j => j.InputPath == p)) continue;
            AudioJobs.Add(new ConvertJob { Kind = JobEncode, InputPath = p });
        }
    }

    [RelayCommand]
    private void AddWemFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 wem 文件（可多选）",
            Filter = "wem 文件|*.wem|所有文件|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddWemFiles(dlg.FileNames);
    }

    public void AddWemFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (WemJobs.Any(j => j.InputPath == p)) continue;
            WemJobs.Add(new ConvertJob { Kind = JobDecode, InputPath = p });
        }
    }

    partial void OnSelectedLibraryEntryChanged(TrackRow? value)
    {
        if (value != null) ExtractFileIdText = value.FileId.ToString();
    }

    // ---- 输出目录 ----

    [RelayCommand]
    private void BrowseOutDir(string? which)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog() != true) return;
        switch (which)
        {
            case "encode": EncodeOutDir = dlg.FolderName; break;
            case "decode": DecodeOutDir = dlg.FolderName; break;
            case "extract": ExtractOutDir = dlg.FolderName; break;
        }
    }

    // ---- 入队与执行 ----

    [RelayCommand]
    private void EnqueueEncodes()
    {
        foreach (var j in AudioJobs)
        {
            if (!File.Exists(j.InputPath)) { j.Status = "失败"; j.Detail = "文件不存在"; continue; }
            if (Queue.Contains(j)) continue;
            j.Status = "排队中"; j.Detail = "";
            Queue.Add(j);
        }
        _ = RunQueueAsync();
    }

    [RelayCommand]
    private void EnqueueDecodes()
    {
        foreach (var j in WemJobs)
        {
            if (!File.Exists(j.InputPath)) { j.Status = "失败"; j.Detail = "文件不存在"; continue; }
            if (Queue.Contains(j)) continue;
            j.ToMp3 = DecodeFormatIndex == 1;
            j.Status = "排队中"; j.Detail = j.ToMp3 ? "目标 mp3" : "目标 wav";
            Queue.Add(j);
        }
        _ = RunQueueAsync();
    }

    [RelayCommand]
    private void EnqueueExtract()
    {
        if (!uint.TryParse(ExtractFileIdText.Trim(), out var fileId))
        {
            QueueStatus = "fileID 必须是非负整数。";
            return;
        }
        var entry = LibraryEntries.FirstOrDefault(e => e.FileId == fileId);
        var job = new ConvertJob
        {
            Kind = JobExtract,
            InputPath = entry != null ? $"{entry.PckName} #{fileId}" : $"fileID {fileId}",
            ExtractFileId = fileId,
            Status = "排队中",
        };
        Queue.Add(job);
        _ = RunQueueAsync();
    }

    [RelayCommand]
    private async Task RunQueueAsync()
    {
        if (IsRunning) return;
        var pending = Queue.Where(j => j.Status == "排队中").ToList();
        if (pending.Count == 0) return;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        QueueTotal = pending.Count;
        QueueDone = 0;
        QueueStatus = "队列执行中…";
        foreach (var job in pending)
        {
            try
            {
                await RunJobAsync(job, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                job.Status = "已取消";
                QueueStatus = $"已取消（完成 {QueueDone}/{QueueTotal}）。";
                break;
            }
            catch (Exception ex)
            {
                job.Status = "失败";
                job.Detail = ex.Message;
                Log($"{job.Kind} {Path.GetFileName(job.InputPath)}：{ex.Message}");
            }
            QueueDone++;
        }
        IsRunning = false;
        if (QueueStatus.StartsWith("队列执行中"))
            QueueStatus = $"完成 {QueueDone}/{QueueTotal}。产物在输出目录。";
    }

    [RelayCommand]
    private void CancelQueue() => _cts?.Cancel();

    private async Task RunJobAsync(ConvertJob job, CancellationToken ct)
    {
        job.Status = "进行中";
        switch (job.Kind)
        {
            case JobEncode:
            {
                string outWem = Path.Combine(EncodeOutDir,
                    Path.GetFileNameWithoutExtension(job.InputPath) + ".wem");
                int rc = await _runner.EncodeAsync(job.InputPath, outWem,
                    job.PresetIndex == 1, Log, ct, loop: job.Loop);
                if (rc != 0 || !File.Exists(outWem)) throw new InvalidOperationException("编码失败（退出码 " + rc + "）");
                job.Status = "完成";
                job.Detail = $"{new FileInfo(outWem).Length:N0} B → {outWem}";
                break;
            }
            case JobDecode:
            {
                string baseName = Path.GetFileNameWithoutExtension(job.InputPath);
                string outWav = Path.Combine(DecodeOutDir, baseName + ".wav");
                int rc = await _runner.DecodeToWavAsync(job.InputPath, outWav, Log, ct);
                if (rc != 0 || !File.Exists(outWav)) throw new InvalidOperationException("解码失败（退出码 " + rc + "）");
                if (job.ToMp3)
                {
                    string outMp3 = Path.Combine(DecodeOutDir, baseName + ".mp3");
                    rc = await _runner.ConvertToMp3Async(outWav, outMp3, Log, ct);
                    if (rc != 0 || !File.Exists(outMp3)) throw new InvalidOperationException("mp3 转换失败（退出码 " + rc + "）");
                    TryDelete(outWav);
                    job.Status = "完成";
                    job.Detail = $"→ {outMp3}";
                }
                else
                {
                    job.Status = "完成";
                    job.Detail = $"→ {outWav}";
                }
                break;
            }
            case JobExtract:
            {
                var entry = LibraryEntries.FirstOrDefault(e => e.FileId == job.ExtractFileId)
                            ?? throw new InvalidOperationException($"fileID {job.ExtractFileId} 不在曲库");
                string outWem = Path.Combine(ExtractOutDir, $"{entry.PckName}.{job.ExtractFileId}.wem");
                await Task.Run(() => PckService.ExtractEntry(entry.Entry.PckPath, job.ExtractFileId, outWem), ct);
                job.Status = "完成";
                job.Detail = $"{new FileInfo(outWem).Length:N0} B → {outWem}";
                break;
            }
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var j in Queue.Where(j => j.Status is "完成" or "失败" or "已取消").ToList())
            Queue.Remove(j);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Log(string line) => App.Dispatch(() => LogLines.Add(line));
}

/// <summary>转换队列任务。</summary>
public partial class ConvertJob : ObservableObject
{
    public string Kind { get; set; } = "";
    public string InputPath { get; set; } = "";
    public uint ExtractFileId { get; set; }
    public bool ToMp3 { get; set; }

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";
    // 仅音频→wem：0=normal 1=chiptune-6k
    [ObservableProperty] private int _presetIndex;
    [ObservableProperty] private bool _loop;

    public string FileName => System.IO.Path.GetFileName(InputPath);
}
