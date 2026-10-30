namespace AdofaiModManager.Models;

/// <summary>
/// 用户配置，保存在 %AppData%\AdofaiModManager\settings.json
/// </summary>
public sealed class AppSettings
{
    /// <summary>游戏安装目录（含 A Dance of Fire and Ice.exe）</summary>
    public string? GamePath { get; set; }

    /// <summary>资源站接口地址</summary>
    public string ApiBaseUrl { get; set; } = "https://www.adofaitools.top";

    /// <summary>用户填写的 API key（仅本地保存）</summary>
    public string? ApiKey { get; set; }

    /// <summary>默认站点的显示名（留空则按地址自动判断：adofaitools → ADOFAITools，其它用域名）</summary>
    public string? SiteName { get; set; }

    /// <summary>自定义站点：显示名（留空用「自定义站点」）</summary>
    public string? CustomSiteName { get; set; }

    /// <summary>自定义站点：接口地址（留空 = 不启用这个来源）</summary>
    public string? CustomSiteUrl { get; set; }

    /// <summary>自定义站点：API key（可留空，看站点是否要求鉴权）</summary>
    public string? CustomSiteApiKey { get; set; }

    /// <summary>主题：System（跟随系统） / Light / Dark</summary>
    public string Theme { get; set; } = "System";

    /// <summary>记住上次停留的页面（新装 / 旧配置缺这个字段时，默认打开「已安装」）</summary>
    public string LastPage { get; set; } = "InstalledModsPage";

    /// <summary>手动指定的游戏版本（留空则自动检测）</summary>
    public string? GameVersionOverride { get; set; }

    /// <summary>是否已完成首次使用向导</summary>
    public bool HasCompletedSetup { get; set; }

    /// <summary>打开软件时检查更新</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// 用户选择「忽略此版本」的 Aurora 版本号。
    /// 记下来后，同一个版本不再重复提示（换了新版本还会提示）。
    /// </summary>
    public string? SkippedAppVersion { get; set; }

    /// <summary>
    /// 检查更新时是否同时查询 GitHub。
    ///
    /// 开启（默认）：GitHub 更新通常比资源站快，能第一时间看到作者的新版；
    ///   但国内访问 GitHub 不稳定，连不上时会自动忽略、只用资源站的结果。
    /// 关闭：只查资源站，检查更快更安静（适合网络环境差的情况）。
    /// </summary>
    public bool CheckGitHubUpdates { get; set; } = true;

    /// <summary>
    /// 是否记录诊断日志（默认开）。
    ///
    /// 日志写在本机用户目录（%LocalAppData%\AdofaiModManager\logs），单文件封顶 2 MB、
    /// 超了自动轮转，并会对 API key / 签名参数做脱敏。关掉后只保留崩溃日志。
    /// </summary>
    public bool DiagnosticLogging { get; set; } = true;

    /// <summary>
    /// 被用户关掉的 mod 来源（存来源 Id；默认站点用 "site"）。
    /// 关掉的来源不会出现在「在线 Mod」的来源列表里，更新检查也会跳过它。
    /// 至少会保留一个来源可用。
    /// </summary>
    public List<string> DisabledSources { get; set; } = [];
}
