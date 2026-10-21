using System.Text.Json;

namespace QxBgmManager.Models;

public record EncodeProgress(string Stage, double Percent, double Elapsed, double Remaining)
{
    public string DisplayText => Stage switch
    {
        "wait" => "等待转码…",
        "prepare" => "正在读取音频…",
        "write" => "正在保存…",
        "done" => "转码完成",
        _ => $"{Percent:F0}% · 已用 {FormatTime(Elapsed)}" +
             (Remaining > 0 ? $" · 预计还需 {FormatTime(Remaining)}" : ""),
    };

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public static bool TryParse(string line, out EncodeProgress? progress)
    {
        progress = null;
        const string prefix = "QXBGM_PROGRESS ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line[prefix.Length..]);
            var root = doc.RootElement;
            var pct = root.GetProperty("percent").GetDouble();
            if (!double.IsFinite(pct)) pct = 0;
            progress = new EncodeProgress(root.GetProperty("stage").GetString() ?? "encode",
                Math.Clamp(pct, 0, 100),
                root.GetProperty("elapsed").GetDouble(), root.GetProperty("remaining").GetDouble());
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (FormatException) { return false; }
    }
}
