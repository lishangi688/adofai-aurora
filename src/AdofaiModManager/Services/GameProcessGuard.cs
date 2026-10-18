using System.Diagnostics;
using System.IO;

namespace AdofaiModManager.Services;

/// <summary>检查游戏是否正在运行，避免安装/切换加载器时文件被占用。</summary>
public static class GameProcessGuard
{
    public static bool IsGameRunning()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
