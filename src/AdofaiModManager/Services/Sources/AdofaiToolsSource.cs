using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services.Sources;

/// <summary>
/// 把现有的 adofaitools 客户端也套进 <see cref="IRemoteSource"/>。
///
/// 目的：证明抽象站得住（连"下载要申请签名直链"这种站点也能归一化），
/// 为以后把「在线 Mod」页统一到同一个接口铺路。
/// 注意：「在线 Mod」页目前仍然走原有代码路径，本类不影响现有行为。
/// </summary>
public sealed class AdofaiToolsSource(
    AdofaiToolsClient client,
    string id = "adofaitools",
    string? displayName = null) : IRemoteSource
{
    public string Id => id;

    /// <summary>
    /// 缓存用的来源标识：自定义站点的 Id 固定是 "custom"，换了站点地址也不变，
    /// 所以要带上接口地址，否则换站后会先显示上一个站点的缓存。
    /// </summary>
    public string CacheId => $"{id}|{client.ApiRoot}";

    public string DisplayName => displayName
        ?? (id == "adofaitools" ? "ADOFAITools" : "自定义站点");

    public bool RequiresKey => true;

    /// <summary>资源站的 mod 详情页地址未知，交给 HomepageUrl 兜底。</summary>
    public string? ModPageUrl(string slug) => null;

    /// <summary>
    /// 按设置创建「自定义站点」来源（同款 API 的第三方/自建站）。
    /// 没填地址就返回 null（表示不启用这个来源）。
    /// </summary>
    public static IRemoteSource? CreateCustomSite()
    {
        var settings = AppServices.Settings.Settings;
        if (string.IsNullOrWhiteSpace(settings.CustomSiteUrl))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(settings.CustomSiteName)
            ? "自定义站点"
            : settings.CustomSiteName.Trim();

        return new AdofaiToolsSource(
            new AdofaiToolsClient(settings.CustomSiteUrl.Trim(), settings.CustomSiteApiKey),
            "custom",
            name);
    }

    /// <summary>资源站目前只有 MOD 一种资源类型。</summary>
    public IReadOnlyList<string> Categories { get; } = ["MOD"];

    public async Task<RemoteModPage> GetModsAsync(RemoteModQuery query, CancellationToken ct = default)
    {
        // 排序：界面用与站点无关的枚举，这里翻回资源站的参数
        var sort = query.Sort switch
        {
            RemoteSort.Downloads => "downloads",
            RemoteSort.Favorites => "favorites",
            _ => "updated",
        };

        var page = await client.GetModsAsync(
            Math.Max(1, query.Page),
            Math.Clamp(query.PageSize, 1, 100),
            query.Search,
            query.Category,   // resourceType
            null,             // loader
            sort,
            null,             // featured
            ct);

        var items = page.Items.Select(ToRemoteMod).ToList();
        return new RemoteModPage(items, page.Total, page.Page, page.PageSize);
    }

    public async Task<RemoteModDetail?> GetModDetailAsync(string slug, CancellationToken ct = default)
    {
        var detail = await client.GetModDetailAsync("MOD", slug, ct);

        var versions = detail.Versions
            .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
            .Select(v => new RemoteVersion(
                VersionId: v.VersionId!,
                Changelog: v.Changelog,
                GameVersion: v.Files
                    .SelectMany(f => f.SupportedGames)
                    .Select(g => g.Version)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                IsBeta: v.VersionType?.Contains("beta", StringComparison.OrdinalIgnoreCase) ?? false,
                Platforms: ["windows"],
                DirectUrl: null))   // ADOFAITools 要申请签名直链，现场解析
            .ToList();

        var mod = ToRemoteMod(new ModListItem
        {
            Id = detail.Id,
            ResourceType = detail.ResourceType,
            Slug = detail.Slug,
            DisplayName = detail.DisplayName,
            Summary = detail.Summary,
            Authors = detail.Authors,
            IconUrl = detail.IconUrl,
            DownloadCount = detail.DownloadCount,
            FavoriteCount = detail.FavoriteCount,
            LatestVersion = detail.LatestVersion,
        });

        return new RemoteModDetail(mod, detail.Description, versions);
    }

    public async Task<RemoteDownload?> ResolveDownloadAsync(
        string slug,
        RemoteVersion version,
        string platform,
        CancellationToken ct = default)
    {
        // 资源站不直接给外链：要先按版本找到文件，再申请一个 24 小时有效的签名直链
        var detail = await client.GetModDetailAsync("MOD", slug, ct);

        var file = detail.Versions
            .FirstOrDefault(v => string.Equals(v.VersionId, version.VersionId, StringComparison.OrdinalIgnoreCase))
            ?.Files.FirstOrDefault(f => f.Loaders.Any(l => l.Equals("unitymodmanager", StringComparison.OrdinalIgnoreCase)))
            ?? detail.Versions
                .FirstOrDefault(v => string.Equals(v.VersionId, version.VersionId, StringComparison.OrdinalIgnoreCase))
                ?.Files.FirstOrDefault()
            ?? detail.PreferredFile;

        if (file is null || string.IsNullOrWhiteSpace(file.Id))
        {
            return null;
        }

        var intent = await client.CreateDownloadIntentAsync(file.Id, ct);
        return new RemoteDownload(
            intent.Url,
            intent.FileName ?? file.Name ?? $"{slug}-{version.VersionId}.zip",
            file.Size,
            file.MimeType,
            IsArchive: true);   // ADOFAITools 的文件是服务端管着的，不需要像第三方源那样防"跳到网页"
    }

    private RemoteMod ToRemoteMod(ModListItem item) => new(
        SourceId: Id,
        Id: item.Id,
        Slug: item.Slug,
        Name: item.DisplayName,
        Authors: string.Join("、", item.Authors
            .Select(a => a.Name ?? a.Nickname ?? a.Username ?? string.Empty)
            .Where(x => x.Length > 0)),
        Summary: item.Summary,
        IconUrl: item.IconUrl,
        Categories: item.ResourceType is { Length: > 0 } type ? [type] : [],
        LatestVersion: item.LatestVersion?.VersionId,
        GameVersion: null,
        Downloads: item.DownloadCount,
        Likes: item.FavoriteCount,
        HomepageUrl: null,
        UpdatedAt: item.UpdatedAt,
        Loader: ModLoader.Umm);
}
