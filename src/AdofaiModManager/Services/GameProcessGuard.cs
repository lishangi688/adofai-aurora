using System.Diagnostics;
using System.IO;

namespace AdofaiModManager.Services;

/// <summary>检查游戏是否正在运行，避免安装/切换加载器时文件被占用。</summary>
public static class GameProcessGuard
{
    /// <summary>
    /// 自检（tools\*SmokeTest）用：临时替换"游戏是否在运行"的判断。
    /// 冒烟测试只写临时目录，不碰真实游戏，但开发机上游戏可能正好开着，
    /// 没有这个开关整批用例会全部因为"游戏正在运行"而失败。
    /// </summary>
    public static Func<bool>? Override { get; set; }

    public static bool IsGameRunning()
    {
        if (Override is { } overridden)
        {
            return overridden();
        }

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
