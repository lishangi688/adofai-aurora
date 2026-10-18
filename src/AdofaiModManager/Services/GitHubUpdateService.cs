using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// GitHub 更新通道：
///  1) 优先读 mod 自带的 Repository 字段（UMM 标准 Repository.json）；
///  2) 没有则用 GitHub Releases（取最新 Release 里的 Repository.json 或 zip）。
/// </summary>
public sealed class GitHubUpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly UpdateSourceStore _store;

    public GitHubUpdateService(UpdateSourceStore store) => _store = store;

    // ---- 检查结果缓存 ----
    // 目的：GitHub 未授权接口只有 60 次/小时，启动检查和手动刷新短时间内重复请求会把它打爆。
    private static readonly ConcurrentDictionary<string, (DateTime Time, UpdateCheckResult Result)> ResultCache = new();

    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromMinutes(2);

    // ---- 连通性探测 ----
    // GitHub 在国内经常连不上；先花一次很短的探测，连不上就直接跳过所有 GitHub 检查，
    // 避免"每个 mod 都等一次超时"（12 个 mod × 20 秒会非常难受）。
    private static readonly object ProbeLock = new();

    private static DateTime _probeTime = DateTime.MinValue;

    private static bool _probeResult;

    /// <summary>GitHub 是否可达（结果会缓存：成功 5 分钟 / 失败 60 秒）。</summary>
    public static async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        lock (ProbeLock)
        {
            var ttl = _probeResult ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60);
            if (DateTime.UtcNow - _probeTime < ttl)
            {
                return _probeResult;
            }
        }

        var ok = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await Http.SendAsync(request, cts.Token);

            // 拿到任何响应（包括 403/429）都算"网络能通"
            ok = true;
        }
        catch
        {
            ok = false;
        }

        lock (ProbeLock)
        {
            _probeTime = DateTime.UtcNow;
            _probeResult = ok;
        }

        return ok;
    }

    /// <summary>让下一次探测重新联网（手动检查更新时用）。</summary>
    public static void InvalidateProbe()
    {
        lock (ProbeLock)
        {
            _probeTime = DateTime.MinValue;
        }
    }

    /// <summary>解析某个 mod 的更新来源（手动指定优先）。</summary>
    public UpdateSource ResolveSource(InstalledMod mod)
    {
        var manual = _store.GetOverride(mod.Id);
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var source = ParseUrl(manual, true);
            if (source.Kind != UpdateSourceKind.None)
            {
                return source;
            }
        }

        if (!string.IsNullOrWhiteSpace(mod.Repository))
        {
            var source = ParseUrl(mod.Repository, false);
            if (source.Kind != UpdateSourceKind.None)
            {
                return source;
            }
        }

        if (!string.IsNullOrWhiteSpace(mod.HomePage))
        {
            var repo = TryParseGitHubRepo(mod.HomePage);
            if (repo is not null)
            {
                return new UpdateSource
                {
                    Kind = UpdateSourceKind.GitHubRepo,
                    Owner = repo.Value.Owner,
                    Repo = repo.Value.Repo,
                };
            }
        }

        return new UpdateSource { Kind = UpdateSourceKind.None };
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateSource source, string modId, string? localVersion, CancellationToken ct = default)
    {
        var cacheKey = $"{source.Kind}|{source.Url}|{source.Owner}/{source.Repo}|{modId}|{localVersion}";

        if (ResultCache.TryGetValue(cacheKey, out var hit))
        {
            var ttl = hit.Result.Success ? SuccessCacheTtl : FailureCacheTtl;
            if (DateTime.UtcNow - hit.Time < ttl)
            {
                return hit.Result;
            }
        }

        var result = await CheckCoreAsync(source, modId, localVersion, ct);
        ResultCache[cacheKey] = (DateTime.UtcNow, result);
        return result;
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync(UpdateSource source, string modId, string? localVersion, CancellationToken ct)
    {
        try
        {
            return source.Kind switch
            {
                UpdateSourceKind.RepositoryJson => await CheckRepositoryAsync(source.Url!, modId, localVersion, ct),
                UpdateSourceKind.GitHubRepo => await CheckGitHubAsync(source.Owner!, source.Repo!, modId, localVersion, ct),
                _ => new UpdateCheckResult { Success = false, Message = "该 mod 没有可用的更新源。" },
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Success = false, Message = $"检查失败：{ex.Message}" };
        }
    }

    private static async Task<UpdateCheckResult> CheckRepositoryAsync(
        string url,
        string modId,
        string? localVersion,
        CancellationToken ct,
        bool allowApiFallback = true)
    {
        try
        {
            var json = await GetStringAsync(url, ct);
            return ParseRepositoryJson(json, modId, localVersion);
        }
        catch (Exception ex)
        {
            // raw.githubusercontent.com 在国内经常被 DNS 污染（连不上），
            // 但 api.github.com 通常能通 → 认出是 GitHub 上的文件就改用 API 再试一次
            if (allowApiFallback && TryParseGitHubOwnerRepo(url, out var owner, out var repo))
            {
                try
                {
                    return await CheckGitHubAsync(owner, repo, modId, localVersion, ct);
                }
                catch
                {
                    // 两条路都不通，保留原始错误信息
                }
            }

            throw new InvalidOperationException(ex.Message);
        }
    }

    private static UpdateCheckResult ParseRepositoryJson(string json, string modId, string? localVersion)
    {
        var repository = JsonSerializer.Deserialize<UmmRepositoryFile>(json, JsonOptions);

        if (repository?.Releases is null || repository.Releases.Count == 0)
        {
            return new UpdateCheckResult { Success = false, Message = "Repository.json 里没有版本信息。" };
        }

        var release = repository.Releases.FirstOrDefault(r =>
                          string.Equals(r.Id, modId, StringComparison.OrdinalIgnoreCase))
                      ?? repository.Releases[0];

        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            return new UpdateCheckResult { Success = false, Message = "Repository.json 里没有下载地址。" };
        }

        var remote = release.Version;
        var available = IsNewer(remote, localVersion);

        return new UpdateCheckResult
        {
            Success = true,
            UpdateAvailable = available,
            LocalVersion = localVersion,
            RemoteVersion = remote,
            DownloadUrl = release.DownloadUrl,
            Message = available ? $"发现新版本 {remote}" : $"已是最新（{localVersion ?? "未知"}）",
        };
    }

    private static async Task<UpdateCheckResult> CheckGitHubAsync(string owner, string repo, string modId, string? localVersion, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        var json = await GetStringAsync(api, ct, gitHubApi: true);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        tag = tag?.TrimStart('v', 'V');

        string? repositoryJsonUrl = null;
        string? zipUrl = null;
        string? zipName = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

                if (url is null)
                {
                    continue;
                }

                if (name.Equals("Repository.json", StringComparison.OrdinalIgnoreCase))
                {
                    repositoryJsonUrl ??= url;
                }
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var better = zipUrl is null || name.Contains(modId, StringComparison.OrdinalIgnoreCase);
                    if (better)
                    {
                        zipUrl = url;
                        zipName = name;
                    }
                }
            }
        }

        if (repositoryJsonUrl is not null)
        {
            var result = await CheckRepositoryAsync(repositoryJsonUrl, modId, localVersion, ct, allowApiFallback: false);
            if (result.Success)
            {
                return result;
            }
        }

        if (zipUrl is null)
        {
            return new UpdateCheckResult
            {
                Success = false,
                Message = $"仓库 {owner}/{repo} 的最新 Release 里没有找到可下载的 zip。",
            };
        }

        var available = IsNewer(tag, localVersion);

        return new UpdateCheckResult
        {
            Success = true,
            UpdateAvailable = available,
            LocalVersion = localVersion,
            RemoteVersion = tag,
            DownloadUrl = zipUrl,
            FileName = zipName,
            Message = available
                ? $"发现新版本 {tag}（{zipName}）"
                : $"已是最新（{localVersion ?? "未知"}）",
        };
    }

    private static bool IsNewer(string? remote, string? local)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            return false;
        }

        return local is null || VersionScheme.Compare(remote, local) > 0;
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct, bool gitHubApi = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", gitHubApi ? "application/vnd.github+json" : "application/json");

        using var response = await Http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub 接口访问受限（可能是请求太频繁），请稍后再试。");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("地址不存在（404）。请检查更新源地址是否正确。");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static UpdateSource ParseUrl(string url, bool manual)
    {
        var trimmed = url.Trim();

        var repo = TryParseGitHubRepo(trimmed);
        if (repo is not null &&
            !trimmed.Contains("/raw/", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateSource
            {
                Kind = UpdateSourceKind.GitHubRepo,
                Owner = repo.Value.Owner,
                Repo = repo.Value.Repo,
                IsManual = manual,
            };
        }

        var repositoryUrl = trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed.TrimEnd('/') + "/Repository.json";

        return new UpdateSource
        {
            Kind = UpdateSourceKind.RepositoryJson,
            Url = repositoryUrl,
            IsManual = manual,
        };
    }

    private static (string Owner, string Repo)? TryParseGitHubRepo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return (parts[0], parts[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从任意 GitHub 域名（github.com / raw.githubusercontent.com / codeload.github.com）
    /// 的链接里取出 作者/仓库，用于"raw 访问不通时改走 API"。
    /// </summary>
    private static bool TryParseGitHubOwnerRepo(string url, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com" or "raw.githubusercontent.com" or "codeload.github.com"))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        owner = parts[0];
        repo = parts[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        // 超时不要太长：GitHub 不可达时要尽快失败，不要让"检查更新"卡住
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AdofaiModManager/0.1");
        return client;
    }

    private sealed class UmmRepositoryFile
    {
        public List<UmmRepositoryRelease> Releases { get; set; } = [];
    }

    private sealed class UmmRepositoryRelease
    {
        public string? Id { get; set; }

        public string? Version { get; set; }

        public string? DownloadUrl { get; set; }
    }
}
