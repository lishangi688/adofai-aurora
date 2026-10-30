using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AdofaiModManager.Models;
using AdofaiModManager.Services.Sources;
using Microsoft.Win32;

namespace AdofaiModManager.Services;

/// <summary>Aurora 自身的分发形态：影响更新时推荐下载哪种文件。</summary>
public enum AppDistribution
{
    /// <summary>绿色版（解压即用）</summary>
    Portable,

    /// <summary>安装版（Inno Setup 装到 Program Files 或用户目录）</summary>
    Installed,
}

/// <summary>
/// Aurora 自身的新版本信息。
/// </summary>
/// <param name="Version">新版本号</param>
/// <param name="SourceLabel">来源（资源站 / GitHub）</param>
/// <param name="PageUrl">人看的页面（资源站首页 / GitHub 发布页）</param>
/// <param name="DownloadUrl">资源站的合并包直链（免鉴权签名直链）</param>
/// <param name="FileName">资源站文件名</param>
/// <param name="PortableUrl">GitHub：绿色版 zip</param>
/// <param name="InstallerUrl">GitHub：安装包 exe</param>
/// <param name="IsCombinedPackage">是否为"合并包"（里面同时含 portable/ 与安装包）</param>
public sealed record AppUpdateInfo(
    string Version,
    string SourceLabel,
    string PageUrl,
    string? DownloadUrl = null,
    string? FileName = null,
    string? PortableUrl = null,
    string? InstallerUrl = null,
    bool IsCombinedPackage = false)
{
    /// <summary>按当前形态（安装版 / 绿色版）挑一个合适的下载地址。</summary>
    public string? UrlFor(AppDistribution distribution)
    {
        if (IsCombinedPackage)
        {
            return DownloadUrl;
        }

        return distribution == AppDistribution.Installed
            ? InstallerUrl ?? DownloadUrl
            : PortableUrl ?? DownloadUrl;
    }
}

/// <summary>
/// Aurora 自身的更新检查（两条通道，思路和 mod 一致）：
///   ① **GitHub Releases** —— 作者首发，国内可能需要代理；
///   ② **资源站「工具库」** —— 站长会把 Aurora 也放到资源站上。
///
/// 两边都查，取版本号更高的那个；都查不到就当作"已是最新"（不打扰用户）。
///
/// 这里只做「提示 + 打开下载页」，**不自动替换正在运行的程序**：
/// 安装版装在 Program Files 下需要管理员权限，交给用户手动更新更稳妥。
/// </summary>
public static class AppUpdateService
{
    public const string GitHubOwner = "lishangi688";

    public const string GitHubRepo = "adofai-aurora";

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>当前版本，例如 "0.1"。</summary>
    public static string CurrentVersion =>
        FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

    /// <summary>发布页（GitHub Releases）。</summary>
    public static string ReleasesPageUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";

    /// <summary>仓库地址。</summary>
    public static string RepoUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    /// <summary>检查新版本；没有新版本或检查失败都返回 null。</summary>
    public static async Task<AppUpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // 开发用钩子：指定一个本地更新包，把"检查更新"直接当成发现新版本。
        // 仅当设置了环境变量 AMM_UPDATE_TEST_PACKAGE 时生效，正常使用不会触发。
        if (TestPackagePath is { } testPackage)
        {
            var test = BuildTestUpdate(testPackage);

            // 仍然按版本号判断，避免更新完之后反复提示
            var newer = KernelService.CompareVersions(test.Version, CurrentVersion) > 0;
            AppPaths.AppendDebugLog($"[appupdate] 测试包={testPackage} 包内版本={test.Version} 当前={CurrentVersion} 判定更新={newer}");

            return newer ? test : null;
        }

        AppPaths.AppendDebugLog($"[appupdate] 开始检查（当前 {CurrentVersion}）");

        var current = CurrentVersion;

        var tasks = new List<Task<AppUpdateInfo?>>();

        // 用户关掉了「同时查询 GitHub」就跳过（与 mod 的更新检查保持一致）
        if (AppServices.Settings.Settings.CheckGitHubUpdates)
        {
            tasks.Add(CheckGitHubAsync(ct));
        }

        if (AppServices.CreateApiClient() is { } client && RemoteSources.IsSiteEnabled())
        {
            tasks.Add(CheckSiteAsync(client, ct));
        }

        if (tasks.Count == 0)
        {
            return null;
        }

        var found = await Task.WhenAll(tasks);

        return found
            .Where(info => info is not null)
            .Select(info => info!)
            .Where(info => KernelService.CompareVersions(info.Version, current) > 0)
            .OrderByDescending(info => info.Version, Comparer<string>.Create(KernelService.CompareVersions))
            // 版本号相同时优先资源站：国内更快，而且能拿到免鉴权的直链下载地址
            .ThenByDescending(info => info.SourceLabel == AppServices.SiteDisplayName ? 1 : 0)
            .FirstOrDefault();
    }

    /// <summary>
    /// 当前是「安装版」还是「绿色版」。
    /// 影响更新引导：安装版建议下载安装包；绿色版直接下载 zip 覆盖即可。
    /// </summary>
    public static AppDistribution Distribution => LazyDistribution.Value;

    // 判断要把注册表里所有卸载项扫一遍；进程运行期间不会变，算一次就够
    //（启动会话头、每条崩溃日志、更新提示都要用它）
    private static readonly Lazy<AppDistribution> LazyDistribution = new(DetectDistribution);

    /// <summary>开发用：本地更新包路径（环境变量 AMM_UPDATE_TEST_PACKAGE）</summary>
    private static string? TestPackagePath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("AMM_UPDATE_TEST_PACKAGE");
            return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
        }
    }

    /// <summary>
    /// 开发用：把本地更新包包装成"发现新版本"，用于验证自动更新流程。
    /// 版本号从包内 update.json 读取（读不到就用当前版本 +0.0.1 造一个）。
    /// </summary>
    private static AppUpdateInfo BuildTestUpdate(string packagePath)
    {
        var version = ReadVersionFromPackage(packagePath) ?? BumpPatch(CurrentVersion);

        return new AppUpdateInfo(
            version,
            "本地测试包",
            ReleasesPageUrl,
            DownloadUrl: packagePath,
            FileName: Path.GetFileName(packagePath),
            IsCombinedPackage: packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadVersionFromPackage(string packagePath)
    {
        try
        {
            if (!packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var archive = System.IO.Compression.ZipFile.OpenRead(packagePath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("update.json", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);

            return document.RootElement.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BumpPatch(string version)
    {
        var parts = version.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var patch))
        {
            parts[^1] = (patch + 1).ToString();
            return string.Join('.', parts);
        }

        return version + ".1";
    }

    /// <summary>给用户的更新建议（一句话）。</summary>
    public static string DistributionHint => Distribution == AppDistribution.Installed
        ? "你是「安装版」：会下载安装包并自动打开，按提示完成安装即可。"
        : "你是「绿色版」：会下载 zip 并自动解压覆盖，完成后自动重启。";

    /// <summary>GitHub：取最新 Release 的 tag，并尽量拿到绿色版 zip 与安装包 exe 的直链。</summary>
    private static async Task<AppUpdateInfo?> CheckGitHubAsync(CancellationToken ct)
    {
        try
        {
            var api = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, api);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 404 通常表示还没发过 Release —— 属于正常情况
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;

            var version = tag?.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            // 从 Release 资产里挑出「绿色版 zip」和「安装包 exe」
            string? portable = null;
            string? portableName = null;
            string? installer = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                    var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        installer ??= url;
                    }
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                             !name.Contains("-all", StringComparison.OrdinalIgnoreCase))
                    {
                        portable ??= url;
                        portableName ??= name;
                    }
                }
            }

            return new AppUpdateInfo(
                version,
                "GitHub",
                page ?? ReleasesPageUrl,
                DownloadUrl: null,
                FileName: portableName,
                PortableUrl: portable,
                InstallerUrl: installer);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>资源站：在「工具库」里找 Aurora 自己，并尽量取到一个可直链下载的文件。</summary>
    private static async Task<AppUpdateInfo?> CheckSiteAsync(AdofaiToolsClient client, CancellationToken ct)
    {
        try
        {
            var list = await client.GetToolsAsync(null, 1, 100, ct);
            var tool = list.Items.FirstOrDefault(IsSelf);

            if (tool is null)
            {
                AppPaths.AppendDebugLog("[appupdate] 资源站工具库里没认出 Aurora（名字 / slug 都不匹配）");
                return null;
            }

            var detail = await client.GetToolDetailAsync(tool.Slug, ct);
            var version = PickNewestVersion(detail) ?? tool.LatestVersion?.VersionId;

            AppPaths.AppendDebugLog(
                $"[appupdate] 资源站认出自己：{tool.DisplayName}（slug={tool.Slug}）版本={version ?? "-"}");

            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var baseUrl = AppServices.Settings.Settings.ApiBaseUrl.TrimEnd('/');

            // 资源站不允许直接外链文件（需要鉴权），但可以申请一个「下载意图」，
            // 拿到一个 24 小时有效的签名直链，浏览器无需 key 即可下载。
            string? downloadUrl = null;
            string? fileName = null;

            var newest = detail.Versions
                .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
                .OrderByDescending(v => v.VersionId!, Comparer<string>.Create(KernelService.CompareVersions))
                .FirstOrDefault();

            if (newest?.File is { } file && !string.IsNullOrWhiteSpace(file.Id))
            {
                try
                {
                    var intent = await client.CreateToolDownloadIntentAsync(file.Id, ct);
                    if (!string.IsNullOrWhiteSpace(intent.Url))
                    {
                        downloadUrl = intent.Url;
                        fileName = string.IsNullOrWhiteSpace(intent.FileName) ? file.Name : intent.FileName;
                    }
                }
                catch
                {
                    // 拿不到直链也没关系，退化成"打开资源站"
                }
            }

            return new AppUpdateInfo(
                version,
                AppServices.SiteDisplayName,
                baseUrl,
                DownloadUrl: downloadUrl,
                FileName: fileName,
                IsCombinedPackage: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在工具库里认出 Aurora 自己。
    ///
    /// 只认明确属于我们的名字 / slug，**故意不认单独的 “AMM” 或以 AMM 开头的名字**：
    /// ADOFAI 生态里已经有一个同样叫 AMM 的管理器，把它认成自己会让 Aurora
    /// 提示“有新版本”，甚至去装别人的安装包。
    /// </summary>
    private static bool IsSelf(ToolListItem tool)
    {
        var name = tool.DisplayName ?? string.Empty;
        var slug = tool.Slug ?? string.Empty;

        return name.Contains("Aurora", StringComparison.OrdinalIgnoreCase)
               || name.Contains("ADOFAI Mod Manager", StringComparison.OrdinalIgnoreCase)
               || slug.Contains("adofai-aurora", StringComparison.OrdinalIgnoreCase)
               || slug.Contains("adofai-mod-manager", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一行环境摘要：版本 / 系统 / 架构 / 安装形态。
    /// 会话头、崩溃日志、导出的诊断包都用它 —— 拿到日志一眼就能看出运行环境。
    /// </summary>
    public static string DescribeEnvironment()
    {
        string distribution;
        try
        {
            distribution = Distribution switch
            {
                AppDistribution.Installed => "安装版",
                AppDistribution.Portable => "绿色版",
                _ => "未知形态",
            };
        }
        catch
        {
            distribution = "未知形态";
        }

        return $"Aurora {CurrentVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / "
               + $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} / {distribution}";
    }

    /// <summary>
    /// 判断当前跑的是安装版还是绿色版：
    /// Inno Setup 安装时会在注册表里写卸载项（含 InstallLocation），
    /// 如果该项指向的目录正好是当前程序所在目录，就说明是安装版。
    /// </summary>
    private static AppDistribution DetectDistribution()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(sub);
                    if (key?.GetValue("DisplayName") is not string displayName)
                    {
                        continue;
                    }

                    if (!displayName.Contains("Aurora", StringComparison.OrdinalIgnoreCase)
                        && !displayName.Contains("ADOFAI Mod Manager", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var location = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(location))
                    {
                        // 退而求其次：从卸载命令里把目录抠出来
                        var command = key.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(command))
                        {
                            location = Path.GetDirectoryName(command.Trim().Trim('"'));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(location) &&
                        string.Equals(
                            location!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            exeDir,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return AppDistribution.Installed;
                    }
                }
            }
        }
        catch
        {
            // 读注册表失败就当绿色版（更保守：不会给出"覆盖安装"的错误建议）
        }

        return AppDistribution.Portable;
    }

    /// <summary>接口返回的 versions 不一定按新到旧排序，这里自己挑最新的。</summary>
    private static string? PickNewestVersion(ToolDetail detail) =>
        detail.Versions
            .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
            .OrderByDescending(v => v.VersionId!, Comparer<string>.Create(KernelService.CompareVersions))
            .FirstOrDefault()?.VersionId;

    private static string FormatVersion(Version? version)
    {
        if (version is null)
        {
            return "0.0";
        }

        var text = $"{version.Major}.{version.Minor}";

        // 有第四段时第三段必须保留：1.0.0.5 原来会被格式化成 "1.0.5"
        if (version.Build > 0 || version.Revision > 0)
        {
            text += $".{Math.Max(0, version.Build)}";
        }

        if (version.Revision > 0)
        {
            text += $".{version.Revision}";
        }

        return text;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/0.1");
        return client;
    }
}
