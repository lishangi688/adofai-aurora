using System.IO;

namespace AdofaiModManager.Services;

/// <summary>
/// UMM 的 Mods / UMMMods 目录迁移。
///
/// UMMCompat / UMMBridge 会把 UMM 的 Mods 重定向到 UMMMods：
///  · UMM → MelonLoader(+UMMCompat)：Mods → UMMMods
///  · MelonLoader(+UMMCompat) → UMM：UMMMods → Mods
///
/// 只移动带 Info.json 的目录，避免把 MelonLoader 的普通 DLL / 资源目录搬走。
/// </summary>
public static class UmmModsMigrator
{
    public static int Move(string gamePath, string sourceFolder, string destinationFolder)
    {
        var source = Path.Combine(gamePath, sourceFolder);
        var destination = Path.Combine(gamePath, destinationFolder);

        if (!Directory.Exists(source))
        {
            return 0;
        }

        if (string.Equals(
                Path.GetFullPath(source).TrimEnd('\\', '/'),
                Path.GetFullPath(destination).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        Directory.CreateDirectory(destination);
        var moved = 0;

        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".aurora-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(dir, "Info.json")) &&
                !File.Exists(Path.Combine(dir, "info.json")))
            {
                continue;
            }

            var target = Path.Combine(destination, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                AppPaths.AppendDebugLog($"[loader] 迁移 UMM mod 时目标已存在，跳过：{name}");
                continue;
            }

            try
            {
                Directory.Move(dir, target);
                moved++;
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[loader] 迁移 UMM mod 失败 {name}：{ex.Message}");
            }
        }

        return moved;
    }
}
