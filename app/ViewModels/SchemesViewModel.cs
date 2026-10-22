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
        try
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
        catch (Exception ex) { ExecuteStatus = "读取方案登记失败：" + ex.Message; }
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
        if (selected.Count == 0) { ExecuteStatus = "请先勾选曲目"; return; }
        _cts = new CancellationTokenSource();
        IsExecuting = true;
        DoneCount = 0; TotalCount = selected.Count; ExecuteProgress = 0;
        using var activity = TaskActivityService.Begin("安装方案", () => _cts?.Cancel());
        try
        {
            var previous = InstallRegistryService.Load().FirstOrDefault(s => s.Id == manifest.Id);
            using var prepared = await SchemeInstallService.PrepareAsync(manifest, selected.Select(r => r.Track),
                AppState.Library, _packageZip, null, previous, false, Log, (i, text, p) => App.Dispatch(() =>
                {
                    selected[i].Phase = text; selected[i].Failed = false;
                    ExecuteProgress = 80.0 * (i + (p?.Percent ?? 0) / 100) / TotalCount;
                    activity.Report(text, ExecuteProgress);
                }), _cts.Token);
            var installed = new InstalledScheme { Id = manifest.Id, Name = manifest.Name, Author = manifest.Author,
                Description = manifest.Description, InstalledAt = DateTime.Now };
            await SchemeInstallService.InstallAsync(installed, prepared, (i, text) => App.Dispatch(() =>
            {
                selected[i].Phase = text; selected[i].Detail = "";
                if (text == "完成") DoneCount++;
                ExecuteProgress = 80 + 20.0 * DoneCount / TotalCount;
                activity.Report(text, ExecuteProgress);
            }), _cts.Token);
            ExecuteStatus = $"已安装 {DoneCount} 首音乐";
            activity.Report(ExecuteStatus, 100);
            if (manifest.Wonderland != null) WonderPrompt = $"可登记奇域「{manifest.Wonderland.Name}」";
        }
        catch (Exception ex)
        {
            ExecuteStatus = ex is OperationCanceledException ? "已取消，可继续安装或恢复已完成曲目" : "安装未完成：" + ex.Message;
            foreach (var row in selected.Where(r => r.Phase != "完成"))
            { row.Phase = ex is OperationCanceledException ? "已取消" : "失败"; row.Detail = ExecuteStatus; row.Failed = true; }
            activity.Report(ExecuteStatus, ExecuteProgress);
        }
        finally
        {
            AppState.InvalidateLibrary();
            IsExecuting = false;
            _cts.Dispose(); _cts = null;
            await RefreshInstalledAsync();
        }
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
        if (row == null || row.Busy || IsExecuting) return;
        row.Busy = true;
        using var cts = new CancellationTokenSource();
        using var activity = TaskActivityService.Begin("恢复方案", cts.Cancel);
        try
        {
            bool ok = await SchemeInstallService.UninstallAsync(row.Scheme, cts.Token);
            ExecuteStatus = ok ? $"已恢复「{row.Name}」" : "部分恢复失败，保留未完成记录，可重试";
            activity.Report(ExecuteStatus, ok ? 100 : 0);
        }
        catch (Exception ex) { ExecuteStatus = ex is OperationCanceledException ? "恢复已取消" : ex.Message; activity.Report(ExecuteStatus, 0); }
        finally { row.Busy = false; await RefreshInstalledAsync(); }
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
    public string TrackCountText => $"{Scheme.Tracks.Count} 首 · {Scheme.Status}";

    [ObservableProperty] private bool _busy;

    public InstalledSchemeRow(InstalledScheme scheme) => Scheme = scheme;
}
