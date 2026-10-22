using System.IO;
using System.Windows;
using System.Windows.Media;

namespace QxBgmManager.Services;

/// <summary>试听播放器：MediaPlayer 单例，同一时间只播一条。</summary>
public static class AudioPreviewService
{
    // 懒初始化：MediaPlayer 在首次播放时才创建（避免无 UI 环境/自测进程实例化 COM 组件）
    private static MediaPlayer? _player;

    private static MediaPlayer Player
    {
        get
        {
            if (_player == null)
            {
                _player = new MediaPlayer();
                _player.MediaEnded += (_, _) => Ui(() => { CurrentPath = null; PlaybackChanged?.Invoke(); });
                _player.MediaFailed += (_, _) => Ui(() => { CurrentPath = null; PlaybackChanged?.Invoke(); });
            }
            return _player;
        }
    }

    /// <summary>当前正在播放的 wav 路径；未在播放为 null。</summary>
    public static string? CurrentPath { get; private set; }

    public static event Action? PlaybackChanged;

    public static void Play(string wavPath)
    {
        Player.Open(new Uri(Path.GetFullPath(wavPath)));
        Player.Play();
        CurrentPath = wavPath;
        PlaybackChanged?.Invoke();
    }

    public static void Stop()
    {
        _player?.Stop();
        _player?.Close();
        CurrentPath = null;
        PlaybackChanged?.Invoke();
    }

    public static bool IsPlayingPath(string wavPath)
        => CurrentPath != null &&
           string.Equals(CurrentPath, wavPath, StringComparison.OrdinalIgnoreCase);

    private static void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) { action(); return; }
        app.Dispatcher.Invoke(action);
    }
}
