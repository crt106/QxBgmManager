namespace QxBgmManager.Models;

public sealed record AudioMetadata(double? DurationSeconds, long SizeBytes)
{
    /// <summary>当前替换预设固定 48kHz 双声道，每 64 个采样占 72 字节，非循环头 64 字节。</summary>
    public static long? EstimateWemBytes(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0) return null;
        double frames = Math.Ceiling(value * 48000 / 64);
        return frames <= (long.MaxValue - 64) / 72 ? (long)frames * 72 + 64 : null;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1048576.0:F2} MB",
    };
}
