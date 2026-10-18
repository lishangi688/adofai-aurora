using System.IO;

namespace AdofaiModManager.Services;

/// <summary>
/// UMMBridge 新版发布包的目录整理。
///
/// v1.2.0 的 UMMBridge.zip 里包含：
///   Plugins/UMMBridge.dll
///   __gamename___Data/Managed/UnityModManager/...
///
/// 安装时需要把占位符 __gamename__ 替换成真实游戏目录名，
/// 否则 UMMBridge 找不到 UnityModManager.dll。
/// </summary>
public static class UmmBridgePackage
{
    public const string GameNamePlaceholder = "__gamename__";

    public static bool LooksLikeNewLayout(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Any(path => Path.GetFileName(path)
                    .Contains(GameNamePlaceholder, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 __gamename___Data 之类的目录名改成真实游戏数据目录名。</summary>
    public static void Normalize(string directory, string gamePath)
    {
        var gameName = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName);

        foreach (var dir in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(dir);
            if (!name.Contains(GameNamePlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var newName = name.Replace(
                GameNamePlaceholder,
                gameName,
                StringComparison.OrdinalIgnoreCase);

            var target = Path.Combine(directory, newName);
            if (Directory.Exists(target))
            {
                // 目标已存在时合并内容，不覆盖根目录本身。
                CopyDirectoryContents(dir, target);
                Directory.Delete(dir, true);
            }
            else
            {
                Directory.Move(dir, target);
            }
        }
    }

    private static void CopyDirectoryContents(string source, string target)
    {
        Directory.CreateDirectory(target);
        var sourceFull = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceFull, file);
            var destination = Path.GetFullPath(Path.Combine(targetFull, relative));
            if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
