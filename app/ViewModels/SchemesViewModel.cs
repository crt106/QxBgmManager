using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QxBgmManager.Models;
using QxBgmManager.Services;

namespace QxBgmManager.ViewModels;

/// <summary>方案管理页：打开/执行 .qxbgm.json 与 .qxbgmzip，已安装方案登记/卸载，导出方案。</summary>
public partial class SchemesViewModel : ObservableObject
{
    [ObservableProperty] private string _schemePath = "";
    [ObservableProperty] private bool _manifestLoaded;
    [ObservableProperty] private string _manifestName = "";
    [ObservableProperty] private string _manifestAuthor = "";
    [ObservableProperty] private string _manifestDescription = "";
    [ObservableProperty] private string _manifestMetaText = "";
    [ObservableProperty] private string _warningsText = "";
    [ObservableProperty] private ObservableCollection<SchemeTrackRow> _tracks = new();

    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _executeProgress;
    [ObservableProperty] private string _executeStatus = "";
    [ObservableProperty] private string _wonderPrompt = "";

    [ObservableProperty] private ObservableCollection<InstalledSchemeRow> _installed = new();

    // 导出表单
    [ObservableProperty] private string _exportId = "";
    [ObservableProperty] private string _exportName = "";
    [ObservableProperty] private string _exportAuthor = "";
    [ObservableProperty] private string _exportDescription = "";
    [ObservableProperty] private string _exportStatus = "";

    public ObservableCollection<string> LogLines { get; } = new();

    private SchemeManifest? _manifest;
    private string? _packageZip;
    private CancellationTokenSource? _cts;
    private readonly EncodeService _encoder = new();

    public SchemesViewModel()
    {
        _ = RefreshInstalledAsync();
    }

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        await Task.Run(() =>
        {
            var rows = InstallRegistryService.Load()
                .OrderByDescending(s => s.InstalledAt)
                .Select(s => new InstalledSchemeRow(s))
                .ToList();
            App.Dispatch(() =>
            {
                Installed = new ObservableCollection<InstalledSchemeRow>(rows);
            });
        });
    }

    // ---- 打开方案 ----

    [RelayCommand]
    private void BrowseScheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开方案",
            Filter = "QxBGM 方案|*.qxbgm.json;*.qxbgmzip|所有文件|*.*",
        };
        if (dlg.ShowDialog() == true) SchemePath = dlg.FileName;
    }

    partial void OnSchemePathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
            _ = OpenSchemeAsync(value);
    }

    [RelayCommand]
    private async Task OpenSchemeAsync(string? path)
    {
        string file = path ?? SchemePath;
        ExecuteStatus = "";
        WonderPrompt = "";
        try
        {
            var manifest = await Task.Run(() => SchemeService.LoadManifestFile(file));
            _manifest = manifest;
            _packageZip = file.EndsWith(".qxbgmzip", StringComparison.OrdinalIgnoreCase) ? file : null;

            var root = SettingsService.Current.GameRoot;
            var (ok, message) = await AppState.EnsureLibraryAsync(root);
            if (!ok)
            {
                ExecuteStatus = message;
                return;
            }

            var rows = new List<SchemeTrackRow>();
            foreach (var t in manifest.Tracks)
            {
                var entry = SchemeService.FindTarget(t, AppState.Library);
                var refName = t.TargetRef != null && !string.IsNullOrWhiteSpace(t.TargetRef.Name)
                    ? t.TargetRef.Name
                    : TrackMapService.NameOf(AppState.TrackMap, t.Target.FileId);
                rows.Add(new SchemeTrackRow(t, refName, entry));
            }

            ManifestName = manifest.Name;
            ManifestAuthor = string.IsNullOrWhiteSpace(manifest.Author) ? "未知作者" : manifest.Author;
            ManifestDescription = manifest.Description;
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(manifest.GameVersion)) meta.Add($"游戏版本 {manifest.GameVersion}");
            if (!string.IsNullOrWhiteSpace(manifest.CreatedAt)) meta.Add($"创建 {manifest.CreatedAt}");
            if (manifest.Wonderland != null)
                meta.Add($"关联奇域：{manifest.Wonderland.Name}（{manifest.Wonderland.Id}）");
            meta.Add($"{manifest.Tracks.Count} 条曲目");
            ManifestMetaText = string.Join(" · ", meta);
            WarningsText = manifest.Warnings.Count > 0 ? string.Join(Environment.NewLine, manifest.Warnings) : "";
            Tracks = new ObservableCollection<SchemeTrackRow>(rows);
            ManifestLoaded = true;
            ExecuteStatus = _packageZip != null
                ? "已读取方案包，勾选曲目后安装。"
                : "已读取方案，勾选曲目后安装。";
            Log("已打开方案 " + file);
        }
        catch (Exception ex)
        {
            ManifestLoaded = false;
            Tracks = new ObservableCollection<SchemeTrackRow>();
            ExecuteStatus = "打开方案失败：" + ex.Message;
        }
    }

    // ---- 一键执行 ----

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (_manifest == null || IsExecuting) return;
        var manifest = _manifest;
        var selected = Tracks.Where(t => t.Selected).ToList();
        if (selected.Count == 0)
        {
            ExecuteStatus = "请先勾选要安装的曲目。";
            return;
        }

        _cts = new CancellationTokenSource();
        IsExecuting = true;
        DoneCount = 0;
        ExecuteProgress = 0;
        TotalCount = selected.Count;
        var installed = new InstalledScheme
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Author = manifest.Author,
            Description = manifest.Description,
            InstalledAt = DateTime.Now,
        };
        int patched = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var row in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                TotalCount = selected.Count;
                try
                {
                    await ProcessTrackAsync(manifest, row, installed.Tracks, _cts.Token);
                    if (row.Failed) failed++;
                    else if (row.Phase.StartsWith("跳过")) skipped++;
                    else patched++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    row.Phase = "失败";
                    row.Detail = ex.Message;
                    failed++;
                }
                DoneCount++;
                ExecuteProgress = DoneCount * 100.0 / TotalCount;
            }

            if (installed.Tracks.Count > 0)
            {
                InstallRegistryService.Upsert(installed);
                AppState.InvalidateLibrary();
                Log($"方案 {manifest.Name} 安装完成：打入 {patched} 条，跳过 {skipped} 条，失败 {failed} 条。");
                ExecuteStatus = $"安装完成：打入 {patched} 条，跳过 {skipped} 条，失败 {failed} 条。" +
                                (skipped > 0 ? " 跳过的条目此前已处于补丁状态。" : "");
                if (manifest.Wonderland != null)
                    WonderPrompt = $"本方案关联奇域「{manifest.Wonderland.Name}」（{manifest.Wonderland.Id}），可一键登记到奇域档案。";
            }
            else
            {
                ExecuteStatus = $"没有可登记的曲目（跳过 {skipped}，失败 {failed}）。";
            }
            await RefreshInstalledAsync();
        }
        catch (OperationCanceledException)
        {
            ExecuteStatus = $"已取消（完成 {DoneCount}/{selected.Count}）。已写入的补丁保持有效。";
            if (installed.Tracks.Count > 0)
            {
                InstallRegistryService.Upsert(installed);
                await RefreshInstalledAsync();
            }
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private async Task ProcessTrackAsync(SchemeManifest manifest, SchemeTrackRow row,
        List<InstalledTrack> installedTracks, CancellationToken ct)
    {
        var t = row.Track;
        if (row.Entry == null)
        {
            row.Phase = "失败";
            row.Detail = "目标条目不在当前曲库";
            row.Failed = true;
            return;
        }

        // 1. 音频落地（下载/解包/本地直用 + sha256）
        row.Phase = "下载/定位音频…";
        string audioPath = await SchemeService.ResolveAudioAsync(manifest, t, _packageZip,
            progress => App.Dispatch(() =>
            {
                row.Detail = progress.Total > 0
                    ? $"下载 {progress.Done / 1048576.0:F1}/{progress.Total / 1048576.0:F1} MB"
                    : $"下载 {progress.Done / 1048576.0:F1} MB";
            }), ct);

        // 2. 编码（kind=raw）或直接采用（kind=wem）
        string wemPath;
        if (t.Audio.Kind == SchemeService.KindRaw)
        {
            row.Phase = "编码…";
            row.Detail = "";
            wemPath = SchemeService.RawWemPath(manifest.Id, t);
            var preset = t.Encode?.Preset ?? "normal";
            int rc = await _encoder.EncodeAsync(audioPath, wemPath, PresetService.HzForPreset(preset),
                Log, ct, loop: t.Encode?.Loop ?? false, onProgress: p => App.Dispatch(() =>
                {
                    row.Detail = p.DisplayText;
                    ExecuteProgress = (DoneCount + p.Percent / 100) * 100 / Math.Max(1, TotalCount);
                }));
            if (rc != 0 || !File.Exists(wemPath))
                throw new InvalidOperationException("编码失败（退出码 " + rc + "）");
        }
        else
        {
            wemPath = audioPath;
        }

        // 3. 容量复核（重读条目，避免曲库缓存过期）
        row.Phase = "容量复核…";
        var entry = PckService.GetEntry(row.Entry.PckPath, t.Target.FileId);
        long wemSize = new FileInfo(wemPath).Length;
        if (wemSize > entry.Size)
        {
            row.CapacityResult = $"✗ {wemSize:N0} > {entry.Size:N0} B";
            throw new InvalidOperationException($"产物 {wemSize:N0} B 超出条目容量 {entry.Size:N0} B");
        }
        row.CapacityResult = $"✓ {wemSize:N0} ≤ {entry.Size:N0} B";

        // 4. 写入补丁（已 patched 的跳过）
        if (PckService.IsEntryPatched(entry.PckPath, entry.FileId))
        {
            row.Phase = "跳过（已替换）";
            row.Detail = "请先恢复原曲，再安装方案";
            return;
        }
        ct.ThrowIfCancellationRequested();
        row.Phase = "正在替换…";
        await Task.Run(() => PckService.Patch(entry.PckPath, entry.FileId, wemPath), ct);
        installedTracks.Add(new InstalledTrack
        {
            PckPath = entry.PckPath,
            PckName = entry.PckName,
            FileId = entry.FileId,
        });
        row.Phase = "完成";
        row.Detail = "";
    }

    [RelayCommand]
    private void CancelExecute()
    {
        _cts?.Cancel();
        ExecuteStatus = "正在取消…";
    }

    // ---- 奇域登记 ----

    [RelayCommand]
    private void RegisterWonder()
    {
        if (_manifest?.Wonderland == null) return;
        var list = WonderArchiveService.Load();
        if (!list.Any(w => w.Id == _manifest.Wonderland.Id))
        {
            list.Add(new WonderEntry
            {
                Id = _manifest.Wonderland.Id,
                Name = _manifest.Wonderland.Name,
                Url = _manifest.Wonderland.Url,
                Note = "由方案安装登记",
                SchemeId = _manifest.Id,
            });
            WonderArchiveService.Save(list);
        }
        WonderPrompt = "";
        ExecuteStatus = "已登记到奇域档案（奇域浏览页可查看）。";
    }

    // ---- 卸载 ----

    [RelayCommand]
    private async Task UninstallAsync(InstalledSchemeRow? row)
    {
        if (row == null || row.Busy) return;
        row.Busy = true;
        int restored = 0;
        var errors = new List<string>();
        try
        {
            foreach (var t in row.Scheme.Tracks)
            {
                try
                {
                    await Task.Run(() => PckService.RestoreOne(t.PckPath, t.FileId));
                    restored++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{t.PckName} #{t.FileId}：{ex.Message}");
                }
            }
            InstallRegistryService.Remove(row.Scheme.Id);
            AppState.InvalidateLibrary();
            ExecuteStatus = $"已卸载「{row.Scheme.Name}」：复原 {restored}/{row.Scheme.Tracks.Count} 条" +
                            (errors.Count > 0 ? "；失败 " + string.Join("；", errors.Take(3)) : "");
            await RefreshInstalledAsync();
        }
        finally
        {
            row.Busy = false;
        }
    }

    // ---- 导出 ----

    [RelayCommand]
    private async Task ExportAsync()
    {
        ExportStatus = "";
        try
        {
            var root = SettingsService.Current.GameRoot;
            var (ok, message) = await AppState.EnsureLibraryAsync(root);
            if (!ok)
            {
                ExportStatus = message;
                return;
            }
            var patches = await Task.Run(() => AppState.ScanPatches(root));
            if (patches.Count == 0)
            {
                ExportStatus = "当前没有活动补丁，无内容可导出。请先在替换中心或方案管理页打入补丁。";
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出方案",
                FileName = (string.IsNullOrWhiteSpace(ExportId) ? "my-pack" : ExportId) + ".qxbgmzip",
                Filter = "QxBGM 方案包|*.qxbgmzip",
            };
            if (dlg.ShowDialog() != true) return;

            var items = patches.Select(p => new SchemeService.ExportItem
            {
                PckPath = p.PckPath,
                FileId = p.FileId,
                RefName = TrackMapService.NameOf(AppState.TrackMap, p.FileId),
            }).ToList();
            await Task.Run(() => SchemeService.ExportSchemeZip(dlg.FileName,
                ExportId, ExportName, ExportAuthor, ExportDescription, items));
            ExportStatus = $"已导出 {items.Count} 条曲目 → {dlg.FileName}";
            Log(ExportStatus);
        }
        catch (Exception ex)
        {
            ExportStatus = "导出失败：" + ex.Message;
        }
        finally
        {
            SchemeService.CleanupTempFiles();
        }
    }

    private void Log(string line) => App.Dispatch(() => LogLines.Add(line));
}

/// <summary>方案清单行。</summary>
public partial class SchemeTrackRow : ObservableObject
{
    public SchemeTrack Track { get; }
    public uint FileId => Track.Target.FileId;
    public string RefName { get; }
    public string KindText => Track.Audio.Kind == SchemeService.KindWem ? "直接安装" : "安装时转码";
    public string PresetText => Track.Audio.Kind == SchemeService.KindWem
        ? "—"
        : (PresetService.GetPresets().FirstOrDefault(p => p.Id == (Track.Encode?.Preset ?? "normal"))?.Name ?? "普通音乐")
          + (Track.Encode?.Loop == true ? "，全曲循环" : "");
    /// <summary>打开方案时的当前条目状态。</summary>
    public string EntryStatus { get; }

    [ObservableProperty] private bool _selected = true;
    [ObservableProperty] private string _phase = "待执行";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _capacityResult = "待复核";
    [ObservableProperty] private bool _failed;

    /// <summary>定位到的曲库条目；null = 目标不存在。</summary>
    public Models.PckEntry? Entry { get; }

    public SchemeTrackRow(SchemeTrack track, string refName, Models.PckEntry? entry)
    {
        Track = track;
        RefName = refName;
        Entry = entry;
        EntryStatus = entry == null ? "✗ 不在曲库" :
            PckService.IsEntryPatched(entry.PckPath, entry.FileId) ? "已替换" : "原曲";
        if (entry == null) Selected = false;
    }
}

/// <summary>已安装方案行。</summary>
public partial class InstalledSchemeRow : ObservableObject
{
    public InstalledScheme Scheme { get; }
    public string Name => Scheme.Name;
    public string IdText => Scheme.Id;
    public string TimeText => Scheme.InstalledAt.ToString("yyyy-MM-dd HH:mm");
    public string TrackCountText => $"{Scheme.Tracks.Count} 首";

    [ObservableProperty] private bool _busy;

    public InstalledSchemeRow(InstalledScheme scheme) => Scheme = scheme;
}
