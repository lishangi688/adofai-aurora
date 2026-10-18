using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 在线 Mod 文件选择：同一个版本可能同时提供 UMM / MelonLoader / BepInEx 文件。
/// 优先挑当前环境真正能用的加载器版本，而不是无脑选第一个 UMM 文件。
/// </summary>
public static class ModFileSelector
{
    public static ModVersion? PickLatestVersion(ModDetail detail)
    {
        if (detail.Versions is not { Count: > 0 })
        {
            return null;
        }

        var latestId = detail.LatestVersion?.VersionId;
        if (!string.IsNullOrWhiteSpace(latestId))
        {
            var latest = detail.Versions.FirstOrDefault(v =>
                string.Equals(v.VersionId, latestId, StringComparison.OrdinalIgnoreCase));
            if (latest is not null)
            {
                return latest;
            }
        }

        return detail.Versions
            .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
            .OrderByDescending(v => v.VersionId!, Comparer<string>.Create(VersionScheme.Compare))
            .FirstOrDefault()
            ?? detail.Versions[0];
    }

    /// <param name="preferredLoader">
    /// 更新已安装的 mod 时传入它当前所属的加载器：同一个版本常常同时提供 UMM / MelonLoader / BepInEx
    /// 三个文件，更新必须拿"当初装的那一种"，否则会把 mod 更新成另一个加载器的版本。
    /// 传 null = 全新安装，按当前环境挑。
    /// </param>
    public static ModVersionFile? Pick(
        ModVersion? version,
        GameLoaderEnvironment environment,
        ModLoaderKind? preferredLoader = null)
    {
        if (version is null || version.Files is not { Count: > 0 })
        {
            return null;
        }

        var files = version.Files;

        // 更新：先按当前 mod 的加载器找，找不到再退回按环境选。
        if (preferredLoader is { } preferred && preferred != ModLoaderKind.Unknown)
        {
            var same = files.FirstOrDefault(f => HasLoader(f, LoaderKey(preferred)));
            if (same is not null)
            {
                return same;
            }
        }

        // 当前实际注入的加载器优先。UMMCompat 也属于“UMM 能用”。
        if (environment.UmmInjectionInstalled || environment.UmmCompatEnabled)
        {
            var umm = files.FirstOrDefault(f => HasLoader(f, "unitymodmanager"));
            if (umm is not null)
            {
                return umm;
            }
        }

        if (environment.MelonLoaderInstalled)
        {
            var melon = files.FirstOrDefault(f => HasLoader(f, "melonloader"));
            if (melon is not null)
            {
                return melon;
            }
        }

        if (environment.BepInExInstalled)
        {
            var bep = files.FirstOrDefault(f => HasLoader(f, "bepinex"));
            if (bep is not null)
            {
                return bep;
            }
        }

        // 有 UMM 文件但当前注入不是 UMM（例如刚切到 ML、UMM 文件还留着）时，
        // 至少不要选成其它加载器的文件。
        var ummFallback = files.FirstOrDefault(f => HasLoader(f, "unitymodmanager"));
        if (ummFallback is not null)
        {
            return ummFallback;
        }

        // 环境不明时保持旧行为：MelonLoader → 第一个。
        return files.FirstOrDefault(f => HasLoader(f, "melonloader")) ?? files[0];
    }

    private static bool HasLoader(ModVersionFile file, string loader) =>
        file.Loaders?.Any(l => l.Equals(loader, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>加载器 → 资源站文件里使用的名字。</summary>
    private static string LoaderKey(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.UnityModManager => "unitymodmanager",
        ModLoaderKind.MelonLoader => "melonloader",
        ModLoaderKind.BepInEx => "bepinex",
        _ => "unitymodmanager",
    };
}

