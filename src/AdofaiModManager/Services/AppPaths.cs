using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AdofaiModManager.Services;

/// <summary>
/// 统一的路径管理 + 日志。
///
/// 注意：安装到 Program Files 后，程序目录是**只读**的（非管理员写不进去），
/// 所以日志一律写到用户目录，配置/收藏等数据也都在用户目录。
///
/// 日志策略（v0.1.7.1 起）：
///  · 默认就记录（不用再设环境变量），用户反馈问题时直接就能发；
///  · 单个文件封顶，超了自动轮转，磁盘占用有上限；
///  · 写之前统一脱敏（API key / 签名参数 / Bearer）；
///  · 每次启动写一行环境摘要；崩溃日志也带环境摘要。
/// </summary>
public static class AppPaths
{
    private const string FolderName = "AdofaiModManager";

    /// <summary>当前日志上限：2 MB（超过就轮转成 debug.1.log）。</summary>
    private const long DebugLogLimit = 2 * 1024 * 1024;

    /// <summary>崩溃日志上限：1 MB。</summary>
    private const long CrashLogLimit = 1 * 1024 * 1024;

    private static readonly object LogLock = new();

    // 写日志前的脱敏规则：API key（adoftools 形如 adof_sk_xxx）、签名/token 参数、Bearer 头
    private static readonly Regex ApiKeyPattern =
        new(@"adof_sk_[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    private static readonly Regex QuerySecretPattern =
        new(@"([?&](?:token|signature|sig|api_key|apikey|key)=)[^&\s""']+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerPattern =
        new(@"(?<=Bearer\s+)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 导出设置文件时：把 JSON 里的密钥字段整个打码（自定义站点的 key 格式不确定，按字段名兜底）
    private static readonly Regex JsonSecretPattern =
        new(@"(""(?:ApiKey|CustomSiteApiKey|Token)""\s*:\s*"")[^""]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 诊断日志开关（默认开）。用户在「设置 → 关于」里可以关掉；
    /// 关掉后只有崩溃日志（crash.log）还会记录。
    /// </summary>
    public static bool DiagnosticLogEnabled { get; set; } = true;

    /// <summary>漫游数据目录（配置、收藏、更新源、内核缓存）</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>本机数据目录（日志）</summary>
    public static string LocalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string LogDirectory { get; } = Path.Combine(LocalDirectory, "logs");

    /// <summary>缓存根目录（图标 / 接口响应 / 已下载的 mod 包）</summary>
    public static string CacheDirectory { get; } = Path.Combine(LocalDirectory, "cache");

    public static string IconCacheDirectory { get; } = Path.Combine(CacheDirectory, "icons");

    public static string ApiCacheDirectory { get; } = Path.Combine(CacheDirectory, "api");

    public static string ModCacheDirectory { get; } = Path.Combine(CacheDirectory, "mods");

    public static string CrashLogPath { get; } = Path.Combine(LogDirectory, "crash.log");

    public static string DebugLogPath { get; } = Path.Combine(LogDirectory, "debug.log");

    /// <summary>上一次会话的日志（轮转后的备份）。</summary>
    public static string PreviousDebugLogPath { get; } = Path.Combine(LogDirectory, "debug.1.log");

    /// <summary>
    /// 额外的详细日志（环境变量 AMM_SCROLL_DEBUG=1 时开启）。
    /// 只用来挂"额外的调试钩子"，普通诊断日志不依赖它。
    /// </summary>
    public static bool VerboseLogEnabled => Environment.GetEnvironmentVariable("AMM_SCROLL_DEBUG") == "1";

    /// <summary>兼容旧名字：少数"只在详细模式才挂的调试钩子"仍然用它判断。</summary>
    public static bool DebugLogEnabled => VerboseLogEnabled;

    /// <summary>
    /// 会话开始：需要的话轮转旧日志，并写一行带环境摘要的分隔行。
    /// 程序启动时调用一次即可（环境摘要由调用方提供，避免这里依赖上层服务）。
    /// </summary>
    public static void StartSession(string environmentLine)
    {
        if (!DiagnosticLogEnabled)
        {
            return;
        }

        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(DebugLogPath, DebugLogLimit, PreviousDebugLogPath);

                File.AppendAllText(
                    DebugLogPath,
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {environmentLine} ===={Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不能影响启动
        }
    }

    public static void AppendDebugLog(string message)
    {
        if (!DiagnosticLogEnabled)
        {
            return;
        }

        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(DebugLogPath, DebugLogLimit, PreviousDebugLogPath);
                File.AppendAllText(DebugLogPath, Redact(message) + Environment.NewLine);
            }
        }
        catch
        {
            // 忽略：日志写不进去也不该影响主流程
        }
    }

    /// <summary>崩溃日志：带环境摘要，并按 1 MB 轮转。</summary>
    public static void AppendCrashLog(string environmentLine, Exception exception)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(CrashLogPath, CrashLogLimit, Path.Combine(LogDirectory, "crash.1.log"));

                File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {environmentLine}{Environment.NewLine}"
                    + Redact(exception.ToString())
                    + Environment.NewLine + Environment.NewLine);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把敏感信息（API key / 签名参数 / Bearer）替换成 *** 再写进日志。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = ApiKeyPattern.Replace(text, "adof_sk_***");
        result = QuerySecretPattern.Replace(result, "$1***");
        result = BearerPattern.Replace(result, "***");
        result = JsonSecretPattern.Replace(result, "$1***");
        return result;
    }

    /// <summary>
    /// 原子地写一个文本文件：先写同目录下的临时文件，再整体替换。
    /// 直接 WriteAllText 时如果中途断电 / 被杀进程，会留下半截 JSON，
    /// 下次启动读不出来就整份配置作废（连"首次向导已完成"都丢了）。
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 临时文件名带 GUID：两个写者并发时不会互相踩
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, true);
    }

    /// <summary>
    /// 数据文件（设置 / 收藏 / 安装映射 / 更新源）读不出来时，先复制一份 *.broken 再说：
    /// 调用方会换成默认值，下一次保存就把原文件覆盖了，不备份的话用户的数据就彻底找不回来。
    /// </summary>
    public static void BackupBrokenFile(string path, Exception reason)
    {
        try
        {
            File.Copy(path, path + ".broken", true);
        }
        catch
        {
            // 忽略
        }

        AppendDebugLog($"[data] {Path.GetFileName(path)} 无法读取，已备份为 {Path.GetFileName(path)}.broken 并使用默认值：{reason.Message}");
    }

    /// <summary>文件超过上限就轮转（保留一份上一版；没有上一版可留时直接清空）。</summary>
    private static void RotateIfNeeded(string path, long limit, string? previousPath = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < limit)
            {
                return;
            }

            if (previousPath is null)
            {
                File.Delete(path);
                return;
            }

            if (File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }

            File.Move(path, previousPath);
        }
        catch
        {
            // 忽略
        }
    }
}
