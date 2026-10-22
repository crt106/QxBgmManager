using System.IO;
using QxBgmManager.ViewModels;

namespace QxBgmManager.Services;

internal static class ReplacementPreflight
{
    internal static Dictionary<ReplaceJob, string> Check(IReadOnlyList<ReplaceJob> jobs)
    {
        var errors = new Dictionary<ReplaceJob, string>();
        var duplicate = jobs.Where(j => j.Target != null)
            .GroupBy(j => (j.Target!.Entry.PckPath.ToUpperInvariant(), j.Target.FileId))
            .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        foreach (var job in jobs)
        {
            try
            {
                if (!File.Exists(job.SourcePath)) errors[job] = "源文件不存在，请重新选择";
                else if (job.Target == null) errors[job] = "请选择要替换的原曲";
                else if (duplicate.Contains(job)) errors[job] = "这首原曲被多条任务选中，请调整";
                else if (PckService.IsEntryPatched(job.Target.Entry.PckPath, job.Target.FileId))
                    errors[job] = "这首原曲已替换，请先恢复";
                else if (job.EstimatedWemBytes is { } size && size > PckService.GetEntry(job.Target.Entry.PckPath, job.Target.FileId).Size)
                    errors[job] = "原曲容量不足，请换更长的原曲或缩短音频";
            }
            catch (Exception ex) { errors[job] = "无法检查原曲：" + ex.Message; }
        }
        return errors;
    }
}
