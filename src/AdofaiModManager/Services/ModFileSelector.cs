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

    public static ModVersionFile? Pick(ModVersion? version, GameLoaderEnvironment environment)
    {
        if (version is null || version.Files is not { Count: > 0 })
        {
            return null;
        }

        var files = version.Files;

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
}

