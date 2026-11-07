using System.Net;
using System.Net.Http;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>资源站接口调用失败（带用户可读信息）。</summary>
public sealed class AdofaiToolsException(string message) : Exception(message);

/// <summary>
/// ADOFAI Tools 资源站客户端。
/// 文档见 docs/API.md：鉴权 Authorization: Bearer &lt;adof_sk_...&gt;
/// </summary>
public sealed class AdofaiToolsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _apiRoot;
    private readonly string? _apiKey;

    /// <summary>当前用的是不是内置的公共 key（用来给出更准确的限流/失效提示）。</summary>
    private readonly bool _usingBuiltInKey;

    public AdofaiToolsClient(string baseUrl, string? apiKey)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            trimmed = "https://www.adofaitools.top";
        }

        _apiRoot = trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/api";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _usingBuiltInKey = _apiKey is not null
                           && AppSecrets.AdofaiToolsApiKey.Length > 0
                           && string.Equals(_apiKey, AppSecrets.AdofaiToolsApiKey, StringComparison.Ordinal);
    }

    public bool HasKey => _apiKey is not null;

    public string ApiRoot => _apiRoot;

    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DetailCacheTtl = TimeSpan.FromMinutes(30);

    public async Task<ModListPage> GetModsAsync(
        int page = 1,
        int pageSize = 20,
        string? search = null,
        string? resourceType = null,
        string? loader = null,
        string? sort = null,
        bool? featured = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);

        // 列表变化较快：缓存 10 分钟，减少重复请求。
        // 键里要带站点地址：默认站点和自定义站点用的是同一个客户端类，不带的话两边的缓存会互相覆盖
        var cacheKey = ApiCache.Key(
            "mods", _apiRoot, page.ToString(), pageSize.ToString(), search, resourceType, loader, sort, featured?.ToString());

        if (ApiCache.TryGet<ModListPage>(cacheKey, ListCacheTtl, out var cached) && cached is not null)
        {
            return cached;
        }

        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}",
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add("search=" + Uri.EscapeDataString(search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            query.Add("resourceType=" + Uri.EscapeDataString(resourceType.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            query.Add("loader=" + Uri.EscapeDataString(loader.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(sort))
        {
            query.Add("sort=" + Uri.EscapeDataString(sort.Trim()));
        }

        if (featured is true)
        {
            query.Add("featured=true");
        }

        var fresh = await GetAsync<ModListPage>($"/mods?{string.Join('&', query)}", ct);
        ApiCache.Set(cacheKey, fresh);
        return fresh;
    }

    public async Task<ModDetail> GetModDetailAsync(string resourceType, string slug, CancellationToken ct = default)
    {
        var url = $"/mods/{Uri.EscapeDataString(resourceType)}/{Uri.EscapeDataString(slug)}";

        // 详情含版本/文件列表，直接决定"装哪一版"，所以缓存时间短一些；
        // 文件 Id 只在本站有效，键里必须带站点地址
        var cacheKey = ApiCache.Key("detail", _apiRoot, resourceType, slug);

        if (ApiCache.TryGet<ModDetail>(cacheKey, DetailCacheTtl, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var fresh = await GetAsync<ModDetail>(url, ct);
            ApiCache.Set(cacheKey, fresh);
            return fresh;
        }
        catch (AdofaiToolsException)
        {
            // 请求失败（断网等）→ 回退到旧缓存，至少还能看/装上次的版本
            if (ApiCache.TryGetStale<ModDetail>(cacheKey, out var stale) && stale is not null)
            {
                return stale;
            }

            throw;
        }
    }

    public Task<DownloadIntent> CreateDownloadIntentAsync(string fileId, CancellationToken ct = default)
    {
        return PostAsync<DownloadIntent>($"/mod-files/{Uri.EscapeDataString(fileId)}/download-intent", ct);
    }

    // ---- 工具库（用于获取 UMM 内核等）----

    public Task<ToolListPage> GetToolsAsync(
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={Math.Clamp(pageSize, 1, 100)}",
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add("search=" + Uri.EscapeDataString(search.Trim()));
        }

        return GetAsync<ToolListPage>($"/tools?{string.Join('&', query)}", ct);
    }

    public Task<ToolDetail> GetToolDetailAsync(string slug, CancellationToken ct = default)
    {
        return GetAsync<ToolDetail>($"/tools/{Uri.EscapeDataString(slug)}", ct);
    }

    public Task<DownloadIntent> CreateToolDownloadIntentAsync(string fileId, CancellationToken ct = default)
    {
        return PostAsync<DownloadIntent>($"/tool-files/{Uri.EscapeDataString(fileId)}/download-intent", ct);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Get, relativeUrl);
        return await SendAsync<T>(request, ct);
    }

    private async Task<T> PostAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, relativeUrl);
        request.Content = new StringContent(string.Empty);

        return await SendAsync<T>(request, ct);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativeUrl)
    {
        var request = new HttpRequestMessage(method, _apiRoot + relativeUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (_apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        }

        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdofaiToolsException("请求超时，请检查网络或代理设置。");
        }
        catch (HttpRequestException ex)
        {
            AppPaths.AppendDebugLog($"[site] 网络请求失败：{ex.Message}");
            throw new AdofaiToolsException("无法连接站点，请检查网络或代理设置。");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AdofaiToolsException(_usingBuiltInKey
                    ? "内置的公共 API key 已失效（401）——它可能已经被撤销。请到「设置」填写自己的 key。"
                    : "API key 无效或未填写（401）。请到「设置」里填写正确的 key。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;

                // 技术细节进日志，界面只给用户看得懂的说明
                AppPaths.AppendDebugLog($"[site] 接口错误 {code}：{TryExtractMessage(body) ?? response.ReasonPhrase}");

                throw new AdofaiToolsException(code switch
                {
                    429 => "站点请求过于频繁（429）。稍后再试，或在「设置」里填写自己的 key。",
                    403 => _usingBuiltInKey
                        ? "站点拒绝了内置的公共 key（403）——可能已被撤销或触发限制。请在「设置」里填写自己的 key。"
                        : "站点拒绝了请求（403），请检查 API key 是否正确。",
                    >= 500 => $"站点服务器暂时出问题了（{code}），请稍后再试。",
                    404 => "没有找到这个资源（404），可能已被作者删除。",
                    _ => $"站点返回了错误（{code}），请稍后再试。",
                });
            }

            try
            {
                var result = JsonSerializer.Deserialize<T>(body, JsonOptions);
                if (result is null)
                {
                    throw new AdofaiToolsException("接口返回了空数据。");
                }

                return result;
            }
            catch (JsonException ex)
            {
                AppPaths.AppendDebugLog($"[site] 解析接口数据失败：{ex.Message}");
                throw new AdofaiToolsException("站点返回的数据无法解析（接口可能已变动）。");
            }
        }
    }

    private static string? TryExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.ValueKind == JsonValueKind.Array
                    ? string.Join("；", message.EnumerateArray().Select(m => m.GetString()))
                    : message.GetString();
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AdofaiModManager/0.1");
        return client;
    }
}
