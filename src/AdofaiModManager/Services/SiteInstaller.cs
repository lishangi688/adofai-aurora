using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 从资源站安装一个 mod 的公共流程（供「在线 Mod」和「收藏」共用）。
///
/// 会优先用本地缓存的安装包（离线可重装），否则下载并顺手缓存。
/// </summary>
public sealed class SiteInstaller(AdofaiToolsClient client, ModService modService)
{
    public Task<InstallResult> InstallAsync(
        ModDetail detail,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        InstalledMod? target = null)
    {
        var version = ModFileSelector.PickLatestVersion(detail);

        // 更新时优先挑"当前已安装的加载器"那一版文件；全新安装才按环境挑。
        var file = ModFileSelector.Pick(version, modService.Environment, target?.Loader)
                   ?? (target is null ? detail.PreferredFile : null);

        return InstallAsync(detail, file, version?.VersionId, progress, ct, target);
    }

    public async Task<InstallResult> InstallAsync(
        ModDetail detail,
        ModVersionFile? file,
        string? versionId,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        InstalledMod? target = null)
    {
        if (file is null)
        {
            return new InstallResult(false, "该 mod 没有可下载的文件。");
        }

        var cacheName = string.IsNullOrWhiteSpace(versionId) ? null : $"{detail.Slug}-{versionId}";

        // 1) 本地已有这一版的包 → 直接装（不联网）
        if (cacheName is not null && ModService.GetCachedZip(cacheName) is { } cachedZip)
        {
            var fromCache = modService.InstallFromZip(cachedZip, target);
            if (fromCache.Success)
            {
                RememberSource(detail, fromCache);
                return fromCache;
            }
        }

        // 2) 下载并安装（成功后会把包留在缓存里）
        var intent = await client.CreateDownloadIntentAsync(file.Id, ct);
        var result = await modService.InstallFromUrlAsync(intent.Url, progress, ct, cacheName, target);

        RememberSource(detail, result);
        return result;
    }

    /// <summary>记录"资源站 mod ↔ UMM Id"的对应，以及可从 GitHub 更新的线索。</summary>
    private static void RememberSource(ModDetail detail, InstallResult result)
    {
        if (!result.Success || result.ModId is null)
        {
            return;
        }

        AppServices.InstallMap.Set(detail.Id, result.ModId);

        var gitHub = detail.HomepageUrl ?? detail.SourceUrl;
        if (!string.IsNullOrWhiteSpace(gitHub) &&
            gitHub.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            AppServices.UpdateSources.SetOverride(result.ModId, gitHub);
        }
    }
}
