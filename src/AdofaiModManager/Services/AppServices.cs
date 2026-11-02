using System.IO;
using AdofaiModManager.Services.Sources;

namespace AdofaiModManager.Services;

/// <summary>
/// 极简的全局服务容器（后续会替换为正式依赖注入）。
/// </summary>
public static class AppServices
{
    public static SettingsService Settings { get; private set; } = null!;

    public static SteamGameLocator GameLocator { get; private set; } = null!;

    public static InstallMapService InstallMap { get; private set; } = null!;

    public static UpdateSourceStore UpdateSources { get; private set; } = null!;

    public static UpdateCenter Updates { get; private set; } = null!;

    public static FavoritesStore Favorites { get; private set; } = null!;

    /// <summary>当前游戏版本：优先用户手动填写，否则尝试自动读取。</summary>
    public static string? GameVersion
    {
        get
        {
            var manual = Settings.Settings.GameVersionOverride;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                return manual.Trim();
            }

            var path = Settings.Settings.GamePath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return null;
            }

            return GameVersionReader.TryRead(path!);
        }
    }

    /// <summary>
    /// 界面上给默认站点起的名字：
    /// ① 用户填了「站点名称」就用它；② 地址判定是 adofaitools 就显示 ADOFAITools；
    /// ③ 其它地址显示它的域名；④ 没配置就返回 ADOFAITools。
    /// </summary>
    public static string SiteDisplayName
    {
        get
        {
            var settings = Settings.Settings;

            if (!string.IsNullOrWhiteSpace(settings.SiteName))
            {
                return settings.SiteName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
                && Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var uri))
            {
                return uri.Host.Contains("adofaitools", StringComparison.OrdinalIgnoreCase)
                    ? "ADOFAITools"
                    : uri.Host;
            }

            return "ADOFAITools";
        }
    }

    /// <summary>
    /// 按当前设置创建资源站客户端。
    /// 优先级：用户自己填的 key &gt; 内置公共 key（仅默认站点）。
    /// 自定义站点不会拿到内置 key，避免把 ATL 的 key 发给别的站点。
    /// </summary>
    public static AdofaiToolsClient? CreateApiClient()
    {
        var settings = Settings.Settings;
        if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
        {
            return null;
        }

        var key = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key) && IsAdofaiToolsHost(settings.ApiBaseUrl))
        {
            key = AppSecrets.AdofaiToolsApiKey;
        }

        return new AdofaiToolsClient(settings.ApiBaseUrl, key);
    }

    /// <summary>判断地址是不是 ADOFAI Tools（含 www），只有它才使用内置 key。</summary>
    private static bool IsAdofaiToolsHost(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("adofaitools.top", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".adofaitools.top", StringComparison.OrdinalIgnoreCase);
    }

    public static void Initialize()
    {
        // 第三方源接口的磁盘缓存（和资源站共用同一套缓存目录与实现）
        SourceCache.DiskRead = (key, ttl) =>
            ApiCache.TryGet<string>(ApiCache.Key("src", key), ttl, out var cached) ? cached : null;
        SourceCache.DiskWrite = (key, body) => ApiCache.Set(ApiCache.Key("src", key), body);

        // modrift 一个版本可能有多个"按游戏版本分流"的文件，需要当前游戏版本来挑对的
        ModriftSource.GameVersionProvider = () => GameVersion;

        Settings = new SettingsService();
        Settings.Load();

        GameLocator = new SteamGameLocator();
        InstallMap = new InstallMapService();
        UpdateSources = new UpdateSourceStore();
        Updates = new UpdateCenter();
        Favorites = new FavoritesStore();

        // 游戏目录：只在"没设置过 / 设置的那个已经不可用"时才自动填。
        // 以前是"自动检测打分更高就覆盖"，会把用户明确选定的目录顶掉 —— 用户可能
        // 故意指向另一份带 mod 的游戏副本，这种选择不该被自动改。
        var configured = Settings.Settings.GamePath;
        var best = GameLocator.FindBest();

        var configuredUsable = !string.IsNullOrWhiteSpace(configured)
            && Directory.Exists(configured)
            && File.Exists(Path.Combine(configured!, SteamGameLocator.GameExeName));

        if (best is not null &&
            !configuredUsable &&
            !string.Equals(best, configured, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Settings.GamePath = best;
            Settings.Save();
        }
    }
}
