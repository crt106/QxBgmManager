namespace QxBgmManager.Models;

/// <summary>持久化设置（app 旁 appsettings.json）。</summary>
public class AppSettings
{
    /// <summary>游戏根目录（含 YuanShen_Data 那一级，如 F:\...\Genshin Impact Game）。</summary>
    public string GameRoot { get; set; } = "";
    public string FfmpegPath { get; set; } = @"D:\ffmpeg\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe";
    /// <summary>python 可执行文件；默认 "python" 由 PATH 解析。</summary>
    public string PythonPath { get; set; } = "python";
    /// <summary>方案市场索引源：本地索引仓库路径或 git URL（默认远端仓库；老用户已存的本地路径不覆盖）。</summary>
    public string RegistrySource { get; set; } = @"https://github.com/crt106/qxbgm-registry.git";
    public bool ThemeDark { get; set; } = true;
    /// <summary>实验：硬件渲染（默认软件渲染兜底黑屏；硬件模式自动关 Backdrop）。重启生效。</summary>
    public bool RenderHardware { get; set; } = true;
}
