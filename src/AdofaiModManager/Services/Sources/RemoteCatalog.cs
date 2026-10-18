using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services.Sources;

/// <summary>下拉框里的一个来源选项（Source 为 null 表示走原有资源站通道）。</summary>
public sealed record SourceChoice(string Title, IRemoteSource? Source)
{
    public override string ToString() => Title;
}

/// <summary>可用的 mod 来源清单。</summary>
public static class RemoteSources
{
    /// <summary>
    /// 界面下拉框用：第一项是默认站点（走原有代码路径，Source 为 null），
    /// 之后是「自定义站点」（配置了才出现）和内置的第三方来源。
    /// 每次都重新构建，这样在设置里改完能立刻生效。
    /// </summary>
    public static IReadOnlyList<SourceChoice> Choices
    {
        get
        {
            var list = new List<SourceChoice>
            {
                new(AppServices.SiteDisplayName, null),
                new("modrift", new ModriftSource()),
            };

            if (AdofaiToolsSource.CreateCustomSite() is { } custom)
            {
                list.Add(new SourceChoice(custom.DisplayName, custom));
            }

            list.Add(new SourceChoice("TUF", new TufSource()));
            list.Add(new SourceChoice("modlist", new ModlistSource()));

            return list;
        }
    }

    /// <summary>
    /// 程序化使用的来源清单（更新检查等用；不含"默认站点原有通道"）。
    /// 自定义站点没配置就不会出现在这里。
    /// </summary>
    public static IReadOnlyList<IRemoteSource> All
    {
        get
        {
            var list = new List<IRemoteSource>
            {
                new ModriftSource(),
            };

            if (AdofaiToolsSource.CreateCustomSite() is { } custom)
            {
                list.Add(custom);
            }

            list.Add(new TufSource());
            list.Add(new ModlistSource());

            return list;
        }
    }

    /// <summary>按 id 找源（更新时要用它解析下载地址）。</summary>
    public static IRemoteSource? ById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 把归一化模型映射成「在线 Mod」页现有的界面模型，
/// 这样新来源可以直接复用页面已有的列表 / 详情 / 版本模板（资源站那条路径不用动）。
/// </summary>
public static class RemoteCatalog
{
    public static ModListItem ToListItem(RemoteMod mod) => new()
    {
        Id = mod.Id,
        ResourceType = "MOD",
        Slug = mod.Slug,
        DisplayName = mod.Name,
        Summary = mod.Summary,
        Authors = ToAuthors(mod.Authors),
        IconUrl = mod.IconUrl,
        DownloadCount = Clamp(mod.Downloads),
        FavoriteCount = Clamp(mod.Likes),
        LatestVersion = new ModLatestVersion { VersionId = mod.LatestVersion },
    };

    public static ModDetail ToDetail(RemoteModDetail detail) => new()
    {
        Id = detail.Mod.Id,
        ResourceType = "MOD",
        Slug = detail.Mod.Slug,
        DisplayName = detail.Mod.Name,
        Summary = detail.Mod.Summary,
        Authors = ToAuthors(detail.Mod.Authors),
        IconUrl = detail.Mod.IconUrl,
        DownloadCount = Clamp(detail.Mod.Downloads),
        Description = detail.Description,
        SourceUrl = detail.Mod.HomepageUrl,
        HomepageUrl = detail.Mod.HomepageUrl,
        LatestVersion = new ModLatestVersion { VersionId = detail.Mod.LatestVersion },
        Versions = detail.Versions.Select(v => new ModVersion
        {
            Id = v.VersionId,
            VersionId = v.VersionId,
            Changelog = BuildChangelog(v),
            // 远端源的下载地址要现场解析（而且要先校验），所以这里不填 Files，
            // 页面也不会去用 detail.PreferredFile —— 安装走 RemoteInstaller。
        }).ToList(),
    };

    /// <summary>「已安装 / 可更新」角标：远端源没有资源站那种 Id 映射，退化成按名字比对。</summary>
    public static string? LocalStateOf(RemoteMod mod, IReadOnlyDictionary<string, string> installedByKey)
    {
        foreach (var key in Keys(mod))
        {
            if (installedByKey.TryGetValue(key, out var localVersion) && !string.IsNullOrWhiteSpace(localVersion))
            {
                return VersionScheme.IsRemoteNewer(localVersion, mod.LatestVersion) ? "可更新" : "已安装";
            }
        }

        return null;
    }

    /// <summary>用于"这个名字是不是已经装了"的若干写法。</summary>
    public static IEnumerable<string> Keys(RemoteMod mod)
    {
        yield return mod.Name;

        var normalized = UpdateCenter.NormalizeName(mod.Name);
        if (normalized.Length > 0)
        {
            yield return normalized;
        }
    }

    private static string BuildChangelog(RemoteVersion version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(version.GameVersion))
        {
            parts.Add($"游戏版本 {version.GameVersion}");
        }

        if (version.IsBeta)
        {
            parts.Add("测试版");
        }

        if (!string.IsNullOrWhiteSpace(version.Changelog))
        {
            parts.Add(version.Changelog.Trim());
        }

        return string.Join("　·　", parts);
    }

    private static List<ModAuthor> ToAuthors(string? authors) =>
        string.IsNullOrWhiteSpace(authors)
            ? []
            : authors.Split('、', ',', ';')
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .Select(a => new ModAuthor { Name = a })
                .ToList();

    private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);
}

/// <summary>
/// 从第三方源安装一个 mod：解析下载地址 → 校验是不是真的压缩包 → 交给 ModService 下载并安装。
/// </summary>
public sealed class RemoteInstaller(IRemoteSource source, ModService modService)
{
    public async Task<InstallResult> InstallAsync(
        RemoteMod mod,
        RemoteVersion version,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (mod.Loader == ModLoader.MelonLoader && !modService.Environment.MelonLoaderInstalled)
        {
            return new InstallResult(false, "这个 mod 需要 MelonLoader。请先到「加载器」页安装 MelonLoader（或 MelonLoader + UMMBridge），再回来安装。");
        }

        RemoteDownload? download;
        try
        {
            download = await source.ResolveDownloadAsync(mod.Slug, version, "windows", ct);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"解析下载地址失败：{ex.Message}");
        }

        if (download is null)
        {
            return new InstallResult(false, $"{source.DisplayName} 没有给出可下载的地址。");
        }

        // 校验：有的源会把"下载"指向项目主页（Content-Type 是 text/html），必须先挡住
        var probe = await SourceHttp.ProbeAsync(download.Url, download.FileName, ct);
        if (probe is null)
        {
            return new InstallResult(false, $"下载地址不可用（{source.DisplayName}）。");
        }

        if (!probe.IsArchive)
        {
            return new InstallResult(false, $"{source.DisplayName} 给出的不是压缩包（可能是项目主页），无法自动安装。");
        }

        // 有的站点（如 modlist）没有"加载器"字段，但文件名会带 ML 标记（例：Overlayer_ML_win.zip）；
        // 个别 CDN 会把文件名只放在 URL 里，所以两处都看一眼。
        var melonEvidence =
            ModLoaderHeuristics.LooksLikeMelonLoader(probe.FileName)
            || (probe.Url?.Contains("melonloader", StringComparison.OrdinalIgnoreCase) ?? false);

        if (melonEvidence && !modService.Environment.MelonLoaderInstalled)
        {
            var shown = ModLoaderHeuristics.LooksLikeMelonLoader(probe.FileName)
                ? probe.FileName
                : probe.Url ?? probe.FileName;

            return new InstallResult(
                false,
                $"这个 mod 的对应版本是 MelonLoader 版（{shown}）。请先到「加载器」页安装 MelonLoader（或 MelonLoader + UMMBridge）。");
        }

        // 走源自己的下载地址（保留它们的下载计数），ModService 会下载 + 解压 + 安装
        var cacheName = $"{source.Id}-{mod.Slug}-{version.VersionId}";
        return await modService.InstallFromUrlAsync(download.Url, progress, ct, cacheName);
    }
}
