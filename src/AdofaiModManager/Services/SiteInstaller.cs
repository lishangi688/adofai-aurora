using System.IO;
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
        string? expectedId = null)
    {
        return InstallAsync(detail, detail.PreferredFile, detail.LatestVersion?.VersionId, progress, ct, expectedId);
    }

    /// <param name="expectedId">更新已安装 mod 时传入它的 Id：包里的 Id 对不上就拒绝（见 ModService.InstallFromZip）</param>
    public async Task<InstallResult> InstallAsync(
        ModDetail detail,
        ModVersionFile? file,
        string? versionId,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        string? expectedId = null)
    {
        if (file is null)
        {
            return new InstallResult(false, "该 mod 没有可下载的文件。");
        }

        var cacheName = string.IsNullOrWhiteSpace(versionId)
            ? null
            : $"{detail.Slug}-{versionId}-{file.Id}";

        // 1) 本地已有这一版的包 → 先核对站点哈希（旧缓存可能来自别的文件），再直接装
        if (cacheName is not null && ModService.GetCachedZip(cacheName) is { } cachedZip)
        {
            var cacheHashOk = string.IsNullOrWhiteSpace(file.Sha256)
                              || string.Equals(
                                  ModService.ComputeSha256(cachedZip),
                                  file.Sha256.Trim(),
                                  StringComparison.OrdinalIgnoreCase);

            if (cacheHashOk)
            {
                var fromCache = modService.InstallFromZip(cachedZip, expectedId);
                if (fromCache.Success)
                {
                    RememberSource(detail, fromCache);
                    return fromCache;
                }
            }
            else
            {
                // 缓存不是当前文件，删掉重新下载，避免拿基础包去装完整包
                try { File.Delete(cachedZip); } catch { /* 下次缓存清理会处理 */ }
                AppPaths.AppendDebugLog($"[install] 站点缓存哈希不一致，已丢弃：{Path.GetFileName(cachedZip)}");
            }
        }

        // 2) 下载并安装（成功后会把包留在缓存里）
        var intent = await client.CreateDownloadIntentAsync(file.Id, ct);
        var result = await modService.InstallFromUrlAsync(
            intent.Url,
            progress,
            ct,
            cacheName,
            expectedId,
            file.Sha256);

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

        // 只在"还没有手动绑定"时才记：用户手填的更新源就是他的明确选择，
        // 不能每次从资源站重装都拿主页地址把它冲掉
        if (!string.IsNullOrWhiteSpace(gitHub) &&
            gitHub.Contains("github.com", StringComparison.OrdinalIgnoreCase) &&
            AppServices.UpdateSources.GetOverride(result.ModId) is null)
        {
            AppServices.UpdateSources.SetOverride(result.ModId, gitHub);
        }
    }
}
