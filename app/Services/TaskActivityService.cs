using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QxBgmManager.Services;

/// <summary>窗口级任务状态；页面切换不影响进度和取消入口。</summary>
public partial class TaskActivityService : ObservableObject
{
    public static TaskActivityService Current { get; } = new();
    private readonly List<Activity> _activities = new();
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private double _progress;

    public static Activity Begin(string title, Action cancel)
    {
        var activity = new Activity(title, cancel);
        App.Dispatch(() => { Current._activities.Add(activity); Current.HasActivity = true; Current.Refresh(); });
        return activity;
    }

    private void Refresh()
    {
        IsRunning = _activities.Count > 0;
        if (!IsRunning) return;
        Message = string.Join(" · ", _activities.Select(a => a.Title + "：" + a.Message));
        Progress = _activities.Average(a => a.Progress);
    }

    [RelayCommand]
    private void Cancel()
    {
        foreach (var a in _activities.ToList()) a.Cancel();
        Message = "正在取消…";
    }

    public sealed class Activity(string title, Action cancel) : IDisposable
    {
        internal string Title { get; } = title;
        internal string Message { get; private set; } = "准备中…";
        internal double Progress { get; private set; }
        internal Action Cancel { get; } = cancel;
        public void Report(string message, double progress) => App.Dispatch(() =>
        { Message = message; Progress = Math.Clamp(progress, 0, 100); Current.Refresh(); });
        public void Dispose() => App.Dispatch(() =>
        {
            Current._activities.Remove(this);
            Current.Message = Title + "：" + Message;
            Current.Progress = Progress;
            Current.Refresh();
        });
    }
}
