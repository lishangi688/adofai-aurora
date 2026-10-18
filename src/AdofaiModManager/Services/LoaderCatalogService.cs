using System.Net.Http;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 加载器版本目录。UMM 用本地内核 + ADOFAITools；MelonLoader / BepInEx / UMMBridge
/// 直接从 GitHub Releases 读取，失败时退回内置的已知稳定版本。
/// </summary>
public static class LoaderCatalogService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, (DateTime Time, List<LoaderVersionOption> Items)> Cache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public static async Task<List<LoaderVersionOption>> GetUmmVersionsAsync(
        string gamePath,
        CancellationToken ct = default)
    {
        var result = new List<LoaderVersionOption>();

        try
        {
            var loader = new LoaderService(gamePath);
            var kernel = new KernelService(loader);
            foreach (var item in kernel.GetLocalKernels())
            {
                result.Add(new LoaderVersionOption
                {
                    Version = item.Version,
                    Kind = LoaderVersionKind.UmmLocal,
                    Kernel = item,
                });
            }
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[loader] 读取本地 UMM 内核失败：{ex.Message}");
        }

        // 站点最新版本作为可选项（填了 API key 才有意义）。
        try
        {
            var client = AppServices.CreateApiClient();
            if (client is not null)
            {
                var site = await KernelBootstrapper.FindLatestSiteKernelAsync(client, ct);
                if (site is { } info &&
                    result.All(r => !string.Equals(r.Version, info.VersionId, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new LoaderVersionOption
                    {
                        Version = info.VersionId,
                        Kind = LoaderVersionKind.UmmSite,
                        FileId = info.FileId,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[loader] 查询站点 UMM 版本失败：{ex.Message}");
        }

        result.Sort((a, b) => KernelService.CompareVersions(b.Version, a.Version));
        if (result.Count == 0)
        {
            result.Add(new LoaderVersionOption
            {
                Version = "0.33.2",
                Kind = LoaderVersionKind.UmmLocal,
            });
        }

        return result;
    }

    public static Task<List<LoaderVersionOption>> GetMelonLoaderVersionsAsync(CancellationToken ct = default) =>
        GetGitHubVersionsAsync(
            "melonloader",
            "https://api.github.com/repos/LavaGang/MelonLoader/releases?per_page=20",
            LoaderVersionKind.MelonLoader,
            asset => asset.Equals("MelonLoader.x64.zip", StringComparison.OrdinalIgnoreCase),
            new[]
            {
                ("0.7.3", "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip", "MelonLoader.x64.zip"),
                ("0.7.2", "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.2/MelonLoader.x64.zip", "MelonLoader.x64.zip"),
            },
            ct);

    public static Task<List<LoaderVersionOption>> GetBepInExVersionsAsync(CancellationToken ct = default) =>
        GetGitHubVersionsAsync(
            "bepinex",
            "https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=20",
            LoaderVersionKind.BepInEx,
            asset => asset.StartsWith("BepInEx_win_x64_", StringComparison.OrdinalIgnoreCase) &&
                     asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            new[]
            {
                ("5.4.23.5", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip", "BepInEx_win_x64_5.4.23.5.zip"),
                ("5.4.23.4", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.4/BepInEx_win_x64_5.4.23.4.zip", "BepInEx_win_x64_5.4.23.4.zip"),
            },
            ct);

    public static Task<List<LoaderVersionOption>> GetUmmCompatVersionsAsync(CancellationToken ct = default) =>
        GetGitHubVersionsAsync(
            "ummbridge",
            "https://api.github.com/repos/modlist-org/UMMBridge/releases?per_page=20",
            LoaderVersionKind.UmmCompat,
            asset => asset.Equals("UMMBridge.zip", StringComparison.OrdinalIgnoreCase) ||
                     asset.Equals("UMMCompat.zip", StringComparison.OrdinalIgnoreCase),
            new[]
            {
                ("1.2.0", "https://github.com/modlist-org/UMMBridge/releases/download/v1.2.0/UMMBridge.zip", "UMMBridge.zip"),
            },
            ct);

    private static async Task<List<LoaderVersionOption>> GetGitHubVersionsAsync(
        string cacheKey,
        string url,
        LoaderVersionKind kind,
        Func<string, bool> assetFilter,
        IReadOnlyList<(string Version, string Url, string Name)> fallback,
        CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(cacheKey, out var hit) &&
                DateTime.UtcNow - hit.Time < CacheTtl &&
                hit.Items.Count > 0)
            {
                return hit.Items.ToList();
            }

            var result = new List<LoaderVersionOption>();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                request.Headers.TryAddWithoutValidation("User-Agent", "Aurora/0.1.8");

                using var response = await Http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var release in doc.RootElement.EnumerateArray())
                    {
                        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                        {
                            continue;
                        }

                        var tag = release.TryGetProperty("tag_name", out var tagElement)
                            ? tagElement.GetString()?.TrimStart('v', 'V')
                            : null;

                        if (string.IsNullOrWhiteSpace(tag) ||
                            !release.TryGetProperty("assets", out var assets) ||
                            assets.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString()
                                : null;
                            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                                ? urlElement.GetString()
                                : null;

                            if (string.IsNullOrWhiteSpace(name) ||
                                string.IsNullOrWhiteSpace(downloadUrl) ||
                                !assetFilter(name))
                            {
                                continue;
                            }

                            result.Add(new LoaderVersionOption
                            {
                                Version = tag,
                                Kind = kind,
                                DownloadUrl = downloadUrl,
                                FileName = name,
                            });
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[loader] 获取 {cacheKey} 版本失败：{ex.Message}");
            }

            if (result.Count == 0)
            {
                result.AddRange(fallback.Select(item => new LoaderVersionOption
                {
                    Version = item.Version,
                    Kind = kind,
                    DownloadUrl = item.Url,
                    FileName = item.Name,
                }));
            }

            result = result
                .GroupBy(r => r.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(r => r.Version, Comparer<string>.Create(KernelService.CompareVersions))
                .ToList();

            Cache[cacheKey] = (DateTime.UtcNow, result);
            return result.ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/0.1.8");
        return client;
    }
}

