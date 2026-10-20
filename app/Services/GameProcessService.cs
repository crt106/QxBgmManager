using System.Diagnostics;

namespace QxBgmManager.Services;

/// <summary>YuanShen.exe 运行状态检测（补丁热切换/写占用提示用）。</summary>
public static class GameProcessService
{
    public static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("YuanShen").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
