using System.Text;
using System.Text.RegularExpressions;
using AdofaiModManager.Models;
using AdofaiModManager.Services.Sources;

namespace AdofaiModManager.Services;

/// <summary>
/// 全局的"更新中心"：保存各 mod 的更新检查结果，供启动检查、角标提醒和各页面共用。
///
/// 更新来源分两类，都参与判断：
///  ① 资源站（只要配置了站点）—— 与「在线 Mod」页看到的一致；
///  ② mod 自带的 GitHub 更新源（Repository.json / Releases）。
/// </summary>
public sealed class UpdateCenter
{
    private static readonly TimeSpan SiteMapTtl = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, UpdateCheckResult> _results = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SiteModMatch> _siteMap = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _siteMapTime = DateTime.MinValue;

    public IReadOnlyDictionary<string, UpdateCheckResult> Results => _results;

    /// <summary>资源站 mod 映射（键：UMM Id 或显示名）</summary>
    public IReadOnlyDictionary<string, SiteModMatch> SiteMap => _siteMap;

    public int UpdatableCount { get; private set; }

    public bool HasChecked { get; private set; }

    /// <summary>结果变化（检查完成 / 清除 / 站点映射刷新）时触发。</summary>
    public event Action? Changed;

    public UpdateCheckResult? Get(string modId) =>
        _results.TryGetValue(modId, out var result) ? result : null;

    /// <summary>取某个已安装 mod 的检查结果（按文件夹区分，避免重复安装时互相覆盖）。</summary>
    public UpdateCheckResult? Get(InstalledMod mod) => Get(mod.UpdateKey);

    /// <summary>在资源站上找与某个已安装 mod 对应的条目（对站长改名有容错）。</summary>
    public SiteModMatch? FindSiteMod(InstalledMod mod)
    {
        // ① 精确匹配：UMM Id / 显示名
        if (_siteMap.TryGetValue(mod.Id, out var byId))
        {
            return byId;
        }

        if (_siteMap.TryGetValue(mod.DisplayName, out var byName))
        {
            return byName;
        }

        // ② 规范化后匹配：站长把名字改成 "AccurateJudgementBar (3.4.0 and 3.3.1)" 这种也认得出
        var normalizedId = NormalizeName(mod.Id);
        if (normalizedId.Length > 0 && _siteMap.TryGetValue(normalizedId, out var normalized))
        {
            return normalized;
        }

        var normalizedDisplay = NormalizeName(mod.DisplayName);
        if (normalizedDisplay.Length > 0 && _siteMap.TryGetValue(normalizedDisplay, out var normalized2))
        {
            return normalized2;
        }

        return null;
    }

    /// <summary>
    /// 名字规范化：去掉括号里的附加说明（如 "(3.4.0 and 3.3.1)"）、空格、点、横线等，
    /// 只留字母和数字并转小写。
    /// 例：`ADOFAI Editor Tweaks - BetterZip` → `adofaieditortweaksbetterzip`
    ///     `ADOFAI.EditorTweaks.BetterZip`      → `adofaieditortweaksbetterzip`（同一个 mod）
    /// </summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = Regex.Replace(value, @"[\(\[（【].*?[\)\]）】]", " ");
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    public void Set(string modId, UpdateCheckResult result)
    {
        _results[modId] = result;
        Recount();
        Changed?.Invoke();
    }

    public void Clear(string modId)
    {
        if (_results.Remove(modId))
        {
            Recount();
            Changed?.Invoke();
        }
    }

    public void Clear(InstalledMod mod) => Clear(mod.UpdateKey);

    public void ClearAll()
    {
        _results.Clear();
        Recount();
        Changed?.Invoke();
    }

    /// <summary>
    /// 手动检查更新时用：把各层缓存清掉，保证这次真的联网。
    /// 覆盖：本中心的内存映射、接口的磁盘缓存、第三方源的进程内缓存、GitHub 探测结果。
    /// 否则点第二次会全部命中缓存，秒回"检查完成"，看起来像没检查。
    /// </summary>
    public void InvalidateCaches()
    {
        _siteMap.Clear();
        _siteMapTime = DateTime.MinValue;

        _remoteMap.Clear();
        _remoteMapTime = DateTime.MinValue;

        ApiCache.Clear();
        SourceCache.Clear();
        GitHubUpdateService.InvalidateProbe();
    }

    /// <summary>
    /// 拉取/刷新「资源站 mod 映射」。10 分钟内重复调用直接返回。
    /// 映射更新后会触发 Changed，让页面刷新（显示更新源与图标）。
    /// </summary>
    public async Task EnsureSiteMapAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force &&
            _siteMap.Count > 0 &&
            DateTime.UtcNow - _siteMapTime < SiteMapTtl)
        {
            return;
        }

        var client = BuildSiteClient();
        if (client is null)
        {
            return;
        }

        var map = await TryFetchSiteVersionsAsync(client, ct);
        if (map is null || map.Count == 0)
        {
            return;
        }

        _siteMap.Clear();
        foreach (var pair in map)
        {
            _siteMap[pair.Key] = pair.Value;
        }

        _siteMapTime = DateTime.UtcNow;
        Changed?.Invoke();
    }

    /// <summary>
    /// 检查全部已安装 mod 的更新。
    ///
    /// 双来源策略：资源站 + GitHub 都查，**取版本更高的那个**；版本相同优先资源站
    /// （国内更快，也给站长贡献下载量）。GitHub 连不上时静默降级，只用资源站的结果。
    /// </summary>
    public async Task CheckAllAsync(
        IReadOnlyList<InstalledMod> mods,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        var github = new GitHubUpdateService(AppServices.UpdateSources);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (forceRefresh)
        {
            InvalidateCaches();
            AppPaths.AppendDebugLog("[check] 手动检查：已清空缓存，强制联网");
        }

        AppPaths.AppendDebugLog($"[check] 开始检查 {mods.Count} 个 mod");

        var gitHubState = await ResolveGitHubStateAsync(ct);
        AppPaths.AppendDebugLog($"[check] GitHub 状态 = {gitHubState}（{stopwatch.ElapsedMilliseconds}ms）");

        // 资源站：一次性取出全部 mod 的最新版本
        await EnsureSiteMapAsync(ct: ct);
        AppPaths.AppendDebugLog($"[check] 站点映射就绪，{_siteMap.Count} 条（{stopwatch.ElapsedMilliseconds}ms）");

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(mod.DisplayName);

            var before = stopwatch.ElapsedMilliseconds;
            _results[mod.UpdateKey] = await CheckOneCoreAsync(mod, github, gitHubState, ct);
            AppPaths.AppendDebugLog($"[check] {mod.Id} 用时 {stopwatch.ElapsedMilliseconds - before}ms");
        }

        HasChecked = true;
        Recount();
        AppPaths.AppendDebugLog($"[check] 循环结束，开始通知界面（{stopwatch.ElapsedMilliseconds}ms）");

        Changed?.Invoke();
        AppPaths.AppendDebugLog($"[check] 完成（{stopwatch.ElapsedMilliseconds}ms）");
    }

    /// <summary>只检查一个 mod（「已安装」页的单条「检查更新」按钮用）。</summary>
    public async Task<UpdateCheckResult> CheckOneAsync(InstalledMod mod, CancellationToken ct = default, bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            InvalidateCaches();
        }

        var github = new GitHubUpdateService(AppServices.UpdateSources);
        var gitHubState = await ResolveGitHubStateAsync(ct);

        await EnsureSiteMapAsync(ct: ct);

        var result = await CheckOneCoreAsync(mod, github, gitHubState, ct);

        _results[mod.UpdateKey] = result;
        Recount();
        Changed?.Invoke();
        return result;
    }

    /// <summary>GitHub 侧的三种状态。</summary>
    private enum GitHubState
    {
        /// <summary>用户关闭了"同时查询 GitHub"</summary>
        Disabled,

        /// <summary>探测到连不上（国内常见）</summary>
        Unreachable,

        /// <summary>可以用</summary>
        Ready,
    }

    private static async Task<GitHubState> ResolveGitHubStateAsync(CancellationToken ct)
    {
        if (!AppServices.Settings.Settings.CheckGitHubUpdates)
        {
            return GitHubState.Disabled;
        }

        return await GitHubUpdateService.ProbeAsync(ct)
            ? GitHubState.Ready
            : GitHubState.Unreachable;
    }

    /// <summary>查一个 mod 的两个来源，并合并成一条结果。</summary>
    private async Task<UpdateCheckResult> CheckOneCoreAsync(
        InstalledMod mod,
        GitHubUpdateService github,
        GitHubState gitHubState,
        CancellationToken ct)
    {
        var site = await BuildSiteResultAsync(mod, ct);
        UpdateCheckResult? gitHub = null;
        string? gitHubNote = null;

        if (gitHubState != GitHubState.Disabled)
        {
            var source = github.ResolveSource(mod);
            if (source.Kind != UpdateSourceKind.None)
            {
                // 探测只针对 GitHub 本身；mod 自带的其它 Repository.json（例如 yqloss.net）
                // 不受 GitHub 连通性影响，照常检查。
                var isGitHubHost = source.Kind == UpdateSourceKind.GitHubRepo
                    || (source.Url?.Contains("github", StringComparison.OrdinalIgnoreCase) ?? false);

                if (gitHubState == GitHubState.Unreachable && isGitHubHost)
                {
                    gitHubNote = "GitHub 未连通";
                }
                else
                {
                    var check = await github.CheckAsync(source, mod.Id, mod.Version, ct, mod.Loader);
                    if (check.Success)
                    {
                        check.SourceLabel = source.Kind == UpdateSourceKind.GitHubRepo || isGitHubHost
                            ? "GitHub"
                            : "Repository.json";
                        gitHub = check;
                    }
                    else
                    {
                        gitHubNote = isGitHubHost ? "GitHub 检查失败" : "更新源检查失败";
                    }
                }
            }
        }

        var merged = Merge(mod, site, gitHub, gitHubNote);

        // 第三方源（TUF / modlist）：谁版本高用谁
        var remote = await BuildRemoteResultAsync(mod, ct);
        return remote is null ? merged : Best(merged, remote);
    }

    // ---------------- 第三方源（TUF / modlist）----------------

    private static readonly TimeSpan RemoteMapTtl = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, List<(IRemoteSource Source, RemoteMod Mod)>> _remoteMap = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _remoteMapTime = DateTime.MinValue;

    /// <summary>
    /// 把第三方源的目录拉下来，建立「规范化名字 → (源, mod)」索引。
    /// 各源适配器自带 HTTP 缓存，所以反复调用只会在过期后真正联网；
    /// 某个源连不上就跳过（不影响其它源，也不影响资源站/GitHub 的结果）。
    /// </summary>
    private async Task<Dictionary<string, List<(IRemoteSource Source, RemoteMod Mod)>>> GetRemoteMapAsync(CancellationToken ct)
    {
        if (_remoteMap.Count > 0 && DateTime.UtcNow - _remoteMapTime < RemoteMapTtl)
        {
            return _remoteMap;
        }

        var map = new Dictionary<string, List<(IRemoteSource Source, RemoteMod Mod)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in RemoteSources.All)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                var page = await source.GetModsAsync(new RemoteModQuery(Page: 1, PageSize: 500), timeout.Token);

                foreach (var mod in page.Items)
                {
                    foreach (var key in RemoteCatalog.Keys(mod))
                    {
                        if (!map.TryGetValue(key, out var list))
                        {
                            map[key] = list = [];
                        }

                        list.Add((source, mod));
                    }
                }
            }
            catch
            {
                // 这个源不可用：跳过
            }
        }

        if (map.Count > 0)
        {
            _remoteMap.Clear();
            foreach (var pair in map)
            {
                _remoteMap[pair.Key] = pair.Value;
            }

            _remoteMapTime = DateTime.UtcNow;
        }

        return _remoteMap;
    }

    /// <summary>第三方源里这个 mod 的最高版本结果（没有就返回 null）。</summary>
    private async Task<UpdateCheckResult?> BuildRemoteResultAsync(InstalledMod mod, CancellationToken ct)
    {
        var map = await GetRemoteMapAsync(ct);

        List<(IRemoteSource Source, RemoteMod Mod)>? candidates = null;
        foreach (var key in new[] { mod.Id, mod.DisplayName, NormalizeName(mod.DisplayName) })
        {
            if (!string.IsNullOrWhiteSpace(key) && map.TryGetValue(key, out var found))
            {
                candidates = found;
                break;
            }
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        UpdateCheckResult? best = null;

        foreach (var (source, remoteMod) in candidates)
        {
            if (string.IsNullOrWhiteSpace(remoteMod.LatestVersion))
            {
                continue;
            }

            // 写法相同走逐片段比较（「年份+周+字母」这类写法才不会漏报）；写法不同只比第一个数字段
            var newer = VersionScheme.Compare(remoteMod.LatestVersion, mod.Version) > 0;

            var result = new UpdateCheckResult
            {
                Success = true,
                UpdateAvailable = newer,
                LocalVersion = mod.Version,
                RemoteVersion = remoteMod.LatestVersion,
                SourceLabel = source.DisplayName,
                RemoteSourceId = source.Id,
                RemoteSlug = remoteMod.Slug,
                Message = newer
                    ? $"{source.DisplayName} 有新版 {remoteMod.LatestVersion}"
                    : $"{source.DisplayName} 已是最新（{remoteMod.LatestVersion}）",
            };

            if (best is null || VersionScheme.Compare(result.RemoteVersion, best.RemoteVersion) > 0)
            {
                best = result;
            }
        }

        return best;
    }

    /// <summary>两个结果取更好的：优先「有更新」的，其次版本更高的（相同则保留 a，维持原有优先级）。</summary>
    private static UpdateCheckResult Best(UpdateCheckResult a, UpdateCheckResult b)
    {
        if (a.UpdateAvailable != b.UpdateAvailable)
        {
            return a.UpdateAvailable ? a : b;
        }

        return VersionScheme.Compare(b.RemoteVersion, a.RemoteVersion) > 0 ? b : a;
    }

    /// <summary>把"已安装 mod"转成资源站侧的检查结果（没有匹配则为 null）。</summary>
    private async Task<UpdateCheckResult?> BuildSiteResultAsync(InstalledMod mod, CancellationToken ct)
    {
        if (FindSiteMod(mod) is not { } site)
        {
            return null;
        }

        // 写法一致：直接逐段比较版本号
        if (VersionScheme.Same(mod.Version, site.Version))
        {
            var newer = VersionScheme.Compare(site.Version, mod.Version) > 0;
            return BuildSiteResult(mod, site, newer, schemeMismatch: false);
        }

        // 写法不同（例：本地 26w40c 是"年份+周"，资源站 26.5.1 是三段数字）：
        // 逐段比较会误判，所以改用「身份判断」——
        // 站点的 latestVersion 是作者手动指定的"最新版本"，只要能在站点版本列表里
        // 认出本地这一版，就能确定自己是不是最新，完全不需要比较版本号大小。
        var identity = await TryMatchVersionIdentityAsync(mod, site, ct);

        if (identity is { } isLatest)
        {
            return BuildSiteResult(mod, site, newer: !isLatest, schemeMismatch: false);
        }

        // 连认都认不出来：只比第一个数字段（宁可少报也不误报），并标注"规则不同"
        var fallbackNewer = VersionScheme.CompareFirstNumber(site.Version, mod.Version) > 0;
        return BuildSiteResult(mod, site, fallbackNewer, schemeMismatch: true);
    }

    private static UpdateCheckResult BuildSiteResult(
        InstalledMod mod,
        SiteModMatch site,
        bool newer,
        bool schemeMismatch)
    {
        return new UpdateCheckResult
        {
            Success = true,
            UpdateAvailable = newer,
            LocalVersion = mod.Version,
            RemoteVersion = site.Version,
            SiteSlug = site.Slug,
            SiteResourceType = site.ResourceType,
            SourceLabel = AppServices.SiteDisplayName,
            VersionSchemeMismatch = schemeMismatch,
            Message = BuildSiteMessage(site.Version, mod.Version, newer, schemeMismatch),
        };
    }

    /// <summary>
    /// 「身份判断」：把本地版本号与站点版本列表逐个比对（忽略大小写、分隔符、括号标签）。
    /// 返回 true = 本地就是作者指定的最新版；false = 本地是列表里的旧版本；null = 列表里认不出这一版。
    /// </summary>
    private static async Task<bool?> TryMatchVersionIdentityAsync(
        InstalledMod mod,
        SiteModMatch site,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mod.Version))
        {
            return null;
        }

        try
        {
            var client = AppServices.CreateApiClient();
            if (client is null)
            {
                return null;
            }

            // 详情接口有缓存（30 分钟），所以只在"写法不同"的少数 mod 上多花一次请求
            var detail = await client.GetModDetailAsync(site.ResourceType, site.Slug, ct);
            var target = NormalizeName(mod.Version);

            if (target.Length == 0)
            {
                return null;
            }

            var matched = detail.Versions
                .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
                .Select(v => NormalizeName(v.VersionId))
                .Where(id => id == target)
                .ToList();

            if (matched.Count == 0)
            {
                return null;
            }

            var latest = NormalizeName(detail.LatestVersion?.VersionId);
            return latest.Length > 0 && matched.All(id => id == latest);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSiteMessage(string remote, string? local, bool newer, bool sameScheme)
    {
        if (!sameScheme)
        {
            return newer
                ? $"ADOFAITools 有新版 {remote}（本地 {local}，两者版本号规则不同）"
                : $"ADOFAITools 为 {remote}，本地为 {local}（版本号规则不同，无法逐段比较）";
        }

        return newer ? $"ADOFAITools 有新版 {remote}" : $"ADOFAITools 已是最新（{remote}）";
    }

    /// <summary>
    /// 合并两个来源：谁版本高用谁；版本相同优先资源站；
    /// GitHub 的异常情况只记一条"次要说明"，不影响主结果。
    /// </summary>
    private static UpdateCheckResult Merge(
        InstalledMod mod,
        UpdateCheckResult? site,
        UpdateCheckResult? gitHub,
        string? gitHubNote)
    {
        if (site is not null && gitHub is not null)
        {
            var cmp = VersionScheme.Compare(gitHub.RemoteVersion, site.RemoteVersion);

            if (cmp > 0)
            {
                // GitHub 更新更快 → 用 GitHub，并注明资源站当前版本
                return Combine(gitHub, site, $"ADOFAITools {site.RemoteVersion}");
            }

            if (cmp < 0)
            {
                return Combine(site, gitHub, $"GitHub {gitHub.RemoteVersion}");
            }

            // 版本一致：优先资源站（下载更快，也不给 GitHub 添流量）
            return Combine(site, gitHub, null);
        }

        if (site is not null)
        {
            return Combine(site, null, gitHubNote);
        }

        if (gitHub is not null)
        {
            return Combine(gitHub, null, null);
        }

        if (gitHubNote is not null)
        {
            return new UpdateCheckResult
            {
                Success = false,
                LocalVersion = mod.Version,
                Message = "无法检查更新源（网络不通或更新源不可用）。",
            };
        }

        return new UpdateCheckResult
        {
            Success = true,
            UpdateAvailable = false,
            LocalVersion = mod.Version,
            Message = "该 mod 没有可用的更新源（可点「绑定 GitHub」手动指定）。",
        };
    }

    /// <summary>
    /// 以 main 为主结果，并保留资源站信息作为下载兜底
    /// （主来源是 GitHub 时，只有资源站那边确实有更新才值得兜底）。
    /// </summary>
    private static UpdateCheckResult Combine(UpdateCheckResult main, UpdateCheckResult? other, string? note)
    {
        var fallback = main.SiteSlug is not null
            ? main
            : other is { UpdateAvailable: true }
                ? other
                : null;

        return new UpdateCheckResult
        {
            Success = main.Success,
            UpdateAvailable = main.UpdateAvailable,
            LocalVersion = main.LocalVersion,
            RemoteVersion = main.RemoteVersion,
            DownloadUrl = main.DownloadUrl,
            FileName = main.FileName,
            SourceLabel = main.SourceLabel,
            Message = main.Message,
            SecondaryNote = note,
            VersionSchemeMismatch = main.VersionSchemeMismatch,
            SiteSlug = fallback?.SiteSlug,
            SiteResourceType = fallback?.SiteResourceType,
        };
    }

    private static AdofaiToolsClient? BuildSiteClient() => AppServices.CreateApiClient();

    /// <summary>
    /// 拉取资源站全部 mod，建立 "UMM Id / 显示名 → 站点条目" 的映射。
    /// 一次拉 100 条（接口上限），翻页取完。
    /// </summary>
    private static async Task<Dictionary<string, SiteModMatch>?> TryFetchSiteVersionsAsync(
        AdofaiToolsClient client,
        CancellationToken ct)
    {
        try
        {
            var map = new Dictionary<string, SiteModMatch>(StringComparer.OrdinalIgnoreCase);
            var page = 1;

            while (true)
            {
                var result = await client.GetModsAsync(page, 100, null, null, null, "updated", null, ct);

                foreach (var item in result.Items)
                {
                    var version = item.LatestVersion?.VersionId;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    var match = new SiteModMatch(
                        version,
                        item.Slug,
                        item.ResourceType ?? "MOD",
                        item.IconUrl,
                        item.DisplayName);

                    // 多个键都登记，尽量抗"站长改名"：
                    // 显示名、slug、以及规范化之后的形式（去掉括号说明和标点）
                    AddKey(map, item.DisplayName, match);
                    AddKey(map, item.Slug, match);
                    AddKey(map, NormalizeName(item.DisplayName), match);
                    AddKey(map, NormalizeName(item.Slug), match);

                    // 装过的走精确映射（最可靠）
                    var ummId = AppServices.InstallMap.GetUmmId(item.Id);
                    if (!string.IsNullOrWhiteSpace(ummId))
                    {
                        AddKey(map, ummId, match);
                        AddKey(map, NormalizeName(ummId), match);
                    }
                }

                if (result.Items.Count == 0 || page * result.PageSize >= result.Total)
                {
                    break;
                }

                page++;
            }

            return map;
        }
        catch (AdofaiToolsException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddKey(Dictionary<string, SiteModMatch> map, string? key, SiteModMatch match)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        map[key] = match;
    }

    private void Recount() =>
        UpdatableCount = _results.Values.Count(r => r.Success && r.UpdateAvailable);
}
