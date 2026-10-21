using CommunityToolkit.Mvvm.ComponentModel;
using QxBgmManager.Models;
using QxBgmManager.Services;
using System.IO;

namespace QxBgmManager.ViewModels;

/// <summary>曲库/导入/转换页共用的一行音乐条目。</summary>
public partial class TrackRow : ObservableObject
{
    public PckEntry Entry { get; }
    /// <summary>曲目信息映射；null = 未识别。</summary>
    public TrackInfo? Info { get; }

    public uint FileId => Entry.FileId;
    public string PckName => Entry.PckName;
    public string SizeText => $"{Entry.Size / 1048576.0:F2} MB";
    public uint CapacityBytes { get; }
    public string PickerTitle => $"{IndexText}  {TrackName}";
    public string ReplacementInfoText => $"{CategoryText} · 原曲 {FormatSeconds(Info is { DurationSec: > 0 } ? Info.DurationSec : CapacityBytes / PckService.BytesPerSecond)} · 可用 {AudioMetadata.FormatBytes(CapacityBytes)}";
    public string ReplacementStateText => IsPatched ? "已替换" : "可替换";

    /// <summary>映射时长优先，否则按字节数估算（54 KB/s）。</summary>
    public double DurationSeconds => Info is { DurationSec: > 0 } ? Info.DurationSec : Entry.DurationSeconds;
    public string DurationText => FormatSeconds(DurationSeconds);

    /// <summary>索引；无映射显示 —。</summary>
    public string IndexText => Info is { Index: > 0 } ? Info.Index.ToString() : "—";

    /// <summary>中文主名（无则空）。</summary>
    public string ChineseName => Info?.SplitName().Chinese ?? "";
    /// <summary>英文副名（无则空）。</summary>
    public string EnglishName => Info?.SplitName().English ?? "";
    /// <summary>曲名主显示：中文名 → 全名 → fileID。</summary>
    public string TrackName => !string.IsNullOrWhiteSpace(ChineseName) ? ChineseName
        : Info is { Name.Length: > 0 } ? Info.Name.Trim()
        : FileId.ToString();

    /// <summary>分类键：101-104；0/-1 = 未识别。</summary>
    public int CategoryKey => Info is { Category: >= 101 and <= 104 } ? Info.Category : -1;
    public string CategoryText => Info?.DisplayCategory ?? "未识别";
    public string CategoryColor => Info?.CategoryColor ?? "#8B90A0";
    public string Album => Info?.Album ?? "";

    /// <summary>行 Tooltip：所属 pck / 专辑 / 英文副名。</summary>
    public string TooltipText
    {
        get
        {
            var parts = new List<string> { $"所属 {PckName}" };
            if (!string.IsNullOrWhiteSpace(Album)) parts.Add($"专辑：{Album}");
            if (!string.IsNullOrWhiteSpace(EnglishName)) parts.Add(EnglishName);
            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <summary>导入/转换页下拉框展示文本，如「10071 难逃之阵 · 16.67 MB / 4:16」。</summary>
    public string CapacityText => $"{IndexText} {TrackName} · {SizeText} / {DurationText}";

    public string WemPreviewPath => Path.Combine(SettingsService.PreviewDir, $"{PckName}.{FileId}.wem");
    public string WavPreviewPath => Path.Combine(SettingsService.PreviewDir, $"{PckName}.{FileId}.wav");

    [ObservableProperty] private bool _isPatched;
    [ObservableProperty] private bool _playing;
    /// <summary>试听解码进行中。</summary>
    [ObservableProperty] private bool _busy;

    public TrackRow(PckEntry entry, TrackInfo? info, bool isPatched, uint? originalSize = null)
    {
        Entry = entry;
        Info = info;
        IsPatched = isPatched;
        CapacityBytes = originalSize ?? entry.Size;
    }

    public static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
