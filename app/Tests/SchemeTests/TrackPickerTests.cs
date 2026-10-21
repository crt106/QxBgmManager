using QxBgmManager.Models;
using QxBgmManager.Services;
using QxBgmManager.ViewModels;

internal static class TrackPickerTests
{
    public static void Run(Action<bool, string> check)
    {
        TrackRow Row(uint id, int category, uint capacity, bool patched = false) => new(
            new PckEntry { FileId = id, Size = capacity, PckName = "BeydMusic0.pck", PckPath = "test.pck" },
            new TrackInfo { FileId = id, Index = 10000 + id, Name = id == 1 ? "探索曲 Forest Song" : "战斗曲 Battle",
                Category = category, Album = "测试专辑", DurationSec = 60 }, patched);
        var small = Row(1, 101, 54064);
        var large = Row(2, 102, 200000);
        var patched = Row(3, 102, 300000, true);
        var model = new TrackPickerViewModel();
        model.SetItems([small, large, patched]);
        check(model.Results.Count == 2, "原曲选择器默认只显示可替换曲目");
        model.SearchText = "探索曲";
        check(model.Results.Count == 1 && ReferenceEquals(model.Results.GetItemAt(0), small), "支持中文曲名搜索");
        model.SearchText = "forest SONG";
        check(model.Results.Count == 1, "支持英文搜索、多关键词与大小写忽略");
        model.SearchText = "10002";
        check(model.Results.Count == 1 && ReferenceEquals(model.Results.GetItemAt(0), large), "支持原曲索引搜索");
        model.SearchText = "2";
        check(model.Results.Count == 1, "支持文件编号搜索");
        model.SearchText = "测试专辑";
        check(model.Results.Count == 2, "支持专辑搜索");
        model.SearchText = "";
        model.Category = model.Categories.Single(c => c.Key == 102);
        check(model.Results.Count == 1 && ReferenceEquals(model.Results.GetItemAt(0), large), "分类与可替换状态可组合筛选");
        model.AvailabilityIndex = 2;
        check(model.Results.Count == 1 && ReferenceEquals(model.Results.GetItemAt(0), patched), "可单独查看已替换曲目");
        model.ResetCommand.Execute(null);
        model.RequiredBytes = AudioMetadata.EstimateWemBytes(1);
        model.FitsOnly = true;
        check(model.Results.Count == 2 && model.Results.Contains(small), "容量筛选包含恰好能容纳的原曲");
        model.RequiredBytes++;
        check(model.Results.Count == 1 && ReferenceEquals(model.Results.GetItemAt(0), large), "容量筛选排除超出空间的曲目");
        model.CurrentSelection = large;
        model.SearchText = "没有这首曲目";
        check(!model.HasResults && ReferenceEquals(model.CurrentSelection, large), "空搜索结果保留已选原曲");
        model.ResetCommand.Execute(null);
        check(model.Results.Count == 2 && ReferenceEquals(model.CurrentSelection, large), "重置筛选也保留已选原曲");
        var original = new TrackRow(new PckEntry { Size = 100, FileId = 4 }, null, true, 200000);
        check(original.CapacityBytes == 200000, "已替换条目的可用空间取自原始备份");

        var job = new ReplaceJob { SourceDurationSeconds = 1, SourceSizeBytes = 1024, IsReadingMetadata = false, Target = small };
        check(job.SourceSummary.Contains("0:01") && job.SourceSummary.Contains("1.0 KB") && job.EstimatedWemBytes == 54064,
            "任务持续显示源时长、源大小和预计转码大小");
        check(job.TargetSummary.Contains("1:00") && !job.CapacityWarning, "任务持续显示所选原曲时长和容量");
        job.SourceDurationSeconds = 2;
        check(job.CapacityWarning && job.CapacityAssessment.Contains("超出容量"), "时长变化自动更新容量不足提示");
        job.Status = "完成";
        job.Target = Row(1, 101, 54064);
        check(job.Status == "完成", "刷新同一原曲的数据不会把已完成任务重置为待执行");
        job.Target = large;
        check(job.Status == "待执行", "主动改选其他原曲会重置任务状态");
        check(AudioMetadata.EstimateWemBytes(double.NaN) == null && AudioMetadata.EstimateWemBytes(-1) == null
              && AudioMetadata.EstimateWemBytes(double.MaxValue) == null, "异常时长不产生容量估算");
        check(AudioMetadataService.ParseProbeDuration("{\"format\":{\"duration\":\"1.25\"}}") == 1.25,
            "读取容器音频时长");
        check(AudioMetadataService.ParseProbeDuration("{\"streams\":[{\"duration\":\"N/A\"},{\"duration\":\"2.5\"}]}") == 2.5,
            "容器未给时长时回退到音轨时长");
        check(AudioMetadataService.ParseProbeDuration("{\"format\":null}") == null
              && AudioMetadataService.ParseProbeDuration("broken") == null, "异常探测输出安全降级");
        check(AudioMetadataService.ParseFfmpegDuration("Duration: 01:02:03.25, start: 0") == 3723.25,
            "ffprobe 缺失时可解析 ffmpeg 文件头中的时长");
    }

    public static async Task CheckMetadataAsync(Action<bool, string> check)
    {
        if (!File.Exists(SettingsService.ResolveFfmpeg())) { Console.WriteLine("SKIP 未配置音频工具，跳过真实时长读取"); return; }
        string path = Path.Combine(Path.GetTempPath(), "qxbgm-时长-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                const int samples = 48000;
                writer.Write("RIFF"u8); writer.Write(36 + samples * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
                writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(samples * 4); writer.Write(new byte[samples * 4]);
            }
            var info = await AudioMetadataService.ReadAsync(path);
            check(info.DurationSeconds is { } duration && Math.Abs(duration - 1) < 0.001 && info.SizeBytes == new FileInfo(path).Length,
                "真实音频探测读取中文路径 WAV 的时长和大小");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await AudioMetadataService.ReadAsync(path, canceled.Token); check(false, "取消时长探测应中止"); }
            catch (OperationCanceledException) { check(true, "清空任务时可取消时长探测"); }
        }
        finally { File.Delete(path); }
    }
}
