using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 安装 / 切换加载器环境。
///
/// 支持组合：
///  · UnityModManager
///  · MelonLoader
///  · BepInEx
///  · MelonLoader + UnityModManager + UMMCompat（UMMCompat 也常被叫作 UMMBridge）
///
/// UMM 与 BepInEx 都使用 Doorstop（winhttp.dll + doorstop_config.ini），不能同时注入；
/// MelonLoader 与它们也会争抢注入文件，因此切换时会先备份旧的注入文件。
/// </summary>
public static class LoaderInstallerService
{
    private static readonly HttpClient Http = CreateHttpClient();

    public static bool IsGameRunning() => GameProcessGuard.IsGameRunning();

    public static async Task<InstallResult> ApplyAsync(
        string gamePath,
        LoaderSelection selection,
        IProgress<string>? progress = null,
        IProgress<DownloadProgress>? downloadProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(gamePath, SteamGameLocator.GameExeName)))
        {
            return new InstallResult(false, $"没找到游戏主程序：{gamePath}");
        }

        if (IsGameRunning())
        {
            return new InstallResult(false, "游戏正在运行。请先退出游戏，再安装或切换加载器。");
        }

        var error = LoaderSelectionRules.Validate(selection);
        if (error is not null)
        {
            return new InstallResult(false, error);
        }

        var before = GameLoaderEnvironment.Detect(gamePath);
        BackupInjectionFiles(gamePath);

        try
        {
            if (selection.MelonLoader)
            {
                progress?.Report("正在安装 MelonLoader…");
                var mlVersion = selection.MelonLoaderVersion
                                ?? (await LoaderCatalogService.GetMelonLoaderVersionsAsync(ct)).FirstOrDefault();
                if (mlVersion is null)
                {
                    return new InstallResult(false, "没有可用的 MelonLoader 版本。");
                }

                await InstallMelonLoaderAsync(gamePath, mlVersion, progress, downloadProgress, ct);

                // MelonLoader 使用 version.dll；把 UMM/BepInEx 的 Doorstop 注入移走，避免争抢。
                RemoveInjectionFiles(gamePath, ModLoaderKind.UnityModManager);
                RemoveInjectionFiles(gamePath, ModLoaderKind.BepInEx);

                if (selection.UmmCompat)
                {
                    progress?.Report("正在安装 UMMBridge…");
                    var compatVersion = selection.UmmCompatVersion
                                        ?? (await LoaderCatalogService.GetUmmCompatVersionsAsync(ct)).FirstOrDefault();
                    if (compatVersion is null)
                    {
                        return new InstallResult(false, "没有可用的 UMMBridge 版本。");
                    }

                    await InstallUmmCompatAsync(gamePath, compatVersion, selection.UmmVersion, progress, downloadProgress, ct);
                    EnableUmmCompat(gamePath, true);
                }
                else
                {
                    EnableUmmCompat(gamePath, false);
                }

                // 从“UMM 单独使用”切到 MelonLoader 时，原来的 Mods 里全是 UMM mod。
                // 装好 MelonLoader 后再迁移，避免安装中途失败时把 mod 提前搬走：
                //  · 有 UMMCompat：UMMCompat 会从 UMMMods 加载它们；
                //  · 没有 UMMCompat：先把它们停放在 UMMMods，避免被 MelonLoader 误加载。
                if (before.ModsRootOwner == ModLoaderKind.UnityModManager)
                {
                    progress?.Report("正在把 UMM mod 迁移到 UMMMods…");
                    UmmModsMigrator.Move(gamePath, "Mods", "UMMMods");
                }
            }
            else if (selection.BepInEx)
            {
                progress?.Report("正在安装 BepInEx…");
                var bepVersion = selection.BepInExVersion
                                 ?? (await LoaderCatalogService.GetBepInExVersionsAsync(ct)).FirstOrDefault();
                if (bepVersion is null)
                {
                    return new InstallResult(false, "没有可用的 BepInEx 版本。");
                }

                await InstallBepInExAsync(gamePath, bepVersion, progress, downloadProgress, ct);
                RemoveInjectionFiles(gamePath, ModLoaderKind.MelonLoader);
                RemoveInjectionFiles(gamePath, ModLoaderKind.UnityModManager);
                EnableUmmCompat(gamePath, false);
            }
            else if (selection.Umm)
            {
                progress?.Report("正在安装 UnityModManager…");
                var ummVersion = selection.UmmVersion
                                 ?? (await LoaderCatalogService.GetUmmVersionsAsync(gamePath, ct)).FirstOrDefault();
                if (ummVersion is null)
                {
                    return new InstallResult(false, "没有可用的 UnityModManager 版本。");
                }

                var deployed = await InstallUmmAsync(gamePath, ummVersion, progress, downloadProgress, ct);
                if (!deployed.Success)
                {
                    return deployed;
                }

                RemoveInjectionFiles(gamePath, ModLoaderKind.MelonLoader);
                RemoveInjectionFiles(gamePath, ModLoaderKind.BepInEx);
                EnableUmmCompat(gamePath, false);

                // 从 MelonLoader(+UMMCompat) 切回 UMM 单独使用：把 UMMMods 里的 mod 移回 Mods。
                progress?.Report("正在把 UMM mod 迁移回 Mods…");
                UmmModsMigrator.Move(gamePath, "UMMMods", "Mods");
            }

            return new InstallResult(true, BuildSuccessMessage(selection));
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[loader] 安装失败：{ex}");
            return new InstallResult(false, $"安装加载器失败：{ex.Message}");
        }
    }

    private static string BuildSuccessMessage(LoaderSelection selection)
    {
        if (selection.MelonLoader && selection.Umm && selection.UmmCompat)
        {
            return "已安装 MelonLoader + UnityModManager（UMMBridge）。"
                   + "\nUMM mod 位于 UMMMods，MelonLoader mod 位于 Mods。";
        }

        if (selection.MelonLoader)
        {
            return "MelonLoader 已安装。mod 放在 Mods，插件放在 Plugins，依赖放在 UserLibs。";
        }

        if (selection.BepInEx)
        {
            return "BepInEx 已安装。插件放在 BepInEx/plugins。";
        }

        return "UnityModManager 已安装。mod 放在 Mods。";
    }

    // ---------------- 各加载器安装 ----------------

    private static async Task<InstallResult> InstallUmmAsync(
        string gamePath,
        LoaderVersionOption option,
        IProgress<string>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken ct)
    {
        var loader = new LoaderService(gamePath);
        var kernel = new KernelService(loader);

        if (option.Kind == LoaderVersionKind.UmmSite)
        {
            if (string.IsNullOrWhiteSpace(option.FileId))
            {
                return new InstallResult(false, "站点内核缺少文件 ID。");
            }

            var client = AppServices.CreateApiClient();
            if (client is null)
            {
                return new InstallResult(false, "未配置 ADOFAITools，无法下载站点内核。");
            }

            progress?.Report($"正在下载 UnityModManager {option.Version}…");
            var intent = await client.CreateToolDownloadIntentAsync(option.FileId, ct);
            var (imported, message) = await kernel.ImportAsync(intent.Url, option.Version, ct, downloadProgress);
            if (imported is null)
            {
                return new InstallResult(false, message);
            }

            return kernel.Deploy(imported);
        }

        if (option.Kernel is not null)
        {
            return kernel.Deploy(option.Kernel);
        }

        progress?.Report("正在使用 Aurora 内置 UnityModManager…");
        return new KernelBootstrapper(loader).InstallBundled();
    }

    private static async Task InstallMelonLoaderAsync(
        string gamePath,
        LoaderVersionOption option,
        IProgress<string>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken ct)
    {
        var zip = await DownloadAsync(option, progress, downloadProgress, ct);
        var staging = Path.Combine(Path.GetTempPath(), $"aurora-ml-loader-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zip, staging, true);
            if (!File.Exists(Path.Combine(staging, "version.dll")) ||
                !Directory.Exists(Path.Combine(staging, "MelonLoader")))
            {
                throw new InvalidOperationException("MelonLoader 包里没有 version.dll 或 MelonLoader 目录。");
            }

            CopyDirectoryContents(staging, gamePath);
            Directory.CreateDirectory(Path.Combine(gamePath, "MelonLoader"));
            File.WriteAllText(
                Path.Combine(gamePath, "MelonLoader", "MelonLoader.version"),
                option.Version);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(zip);
        }
    }

    private static async Task InstallBepInExAsync(
        string gamePath,
        LoaderVersionOption option,
        IProgress<string>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken ct)
    {
        var zip = await DownloadAsync(option, progress, downloadProgress, ct);
        var staging = Path.Combine(Path.GetTempPath(), $"aurora-bep-loader-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zip, staging, true);
            if (!File.Exists(Path.Combine(staging, "BepInEx", "core", "BepInEx.Preloader.dll")))
            {
                throw new InvalidOperationException("BepInEx 包里没有 BepInEx/core/BepInEx.Preloader.dll。");
            }

            CopyDirectoryContents(staging, gamePath);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(zip);
        }
    }

    private static async Task InstallUmmCompatAsync(
        string gamePath,
        LoaderVersionOption option,
        LoaderVersionOption? ummVersion,
        IProgress<string>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken ct)
    {
        var zip = await DownloadAsync(option, progress, downloadProgress, ct);
        var staging = Path.Combine(Path.GetTempPath(), $"aurora-ummcompat-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zip, staging, true);
            UmmBridgePackage.Normalize(staging, gamePath);

            var plugin = FindUmmCompatPlugin(staging);
            if (plugin is null)
            {
                throw new InvalidOperationException("UMMBridge 包里没有 Plugins/UMMBridge.dll（或旧版 UMMCompat.dll）。");
            }

            var isNewBridge = Path.GetFileName(plugin)
                .Equals("UMMBridge.dll", StringComparison.OrdinalIgnoreCase);

            CopyDirectoryContents(staging, gamePath);
            Directory.CreateDirectory(Path.Combine(gamePath, "UMMMods"));

            // 可选：用用户选择的 UMM 内核替换 UMMBridge 自带的 UMM。
            var kernel = ummVersion?.Kernel;
            if (kernel is null &&
                ummVersion?.Kind == LoaderVersionKind.UmmSite &&
                !string.IsNullOrWhiteSpace(ummVersion.FileId))
            {
                var client = AppServices.CreateApiClient();
                if (client is not null)
                {
                    progress?.Report($"正在下载 UnityModManager {ummVersion.Version}…");
                    var intent = await client.CreateToolDownloadIntentAsync(ummVersion.FileId, ct);
                    var kernelService = new KernelService(new LoaderService(gamePath));
                    var (imported, _) = await kernelService.ImportAsync(intent.Url, ummVersion.Version, ct, downloadProgress);
                    kernel = imported;
                }
            }

            if (kernel is not null)
            {
                var dataFolder = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName) + "_Data";
                var ummTarget = isNewBridge
                    ? Path.Combine(gamePath, dataFolder, "Managed", "UnityModManager")
                    : Path.Combine(gamePath, "UserLibs");

                Directory.CreateDirectory(ummTarget);
                foreach (var file in new[] { "UnityModManager.dll", "dnlib.dll", "UnityModManager.xml", "0Harmony.dll" })
                {
                    var source = Path.Combine(kernel.Directory, file);
                    if (File.Exists(source))
                    {
                        File.Copy(source, Path.Combine(ummTarget, file), true);
                    }
                }
            }

            if (!isNewBridge)
            {
                // 旧版 UMMCompat 依赖 UserLibs/Config.xml 里的 UMMMods。
                UmmCompatConfig.Ensure(gamePath);
            }
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(zip);
        }
    }

    // ---------------- 兼容层 / 注入文件 ----------------

    private static string? FindUmmCompatPlugin(string directory)
    {
        foreach (var name in new[] { "UMMBridge.dll", "UMMCompat.dll" })
        {
            var path = Path.Combine(directory, "Plugins", name);
            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                var found = Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            catch
            {
                // 忽略
            }
        }

        return null;
    }

    private static void EnableUmmCompat(string gamePath, bool enabled)
    {
        var plugins = Path.Combine(gamePath, "Plugins");
        var names = new[] { "UMMBridge.dll", "UMMCompat.dll" };

        if (!enabled)
        {
            foreach (var name in names)
            {
                var active = Path.Combine(plugins, name);
                var disabled = active + ".disabled";
                try
                {
                    if (File.Exists(active))
                    {
                        File.Move(active, disabled, true);
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            return;
        }

        // UMMBridge 是当前名称，UMMCompat 是旧名称。
        // 同时启用会重复加载，所以只保留一个（优先 UMMBridge.dll）。
        var preferred = names.FirstOrDefault(name => File.Exists(Path.Combine(plugins, name)))
                        ?? names.FirstOrDefault(name => File.Exists(Path.Combine(plugins, name) + ".disabled"))
                        ?? names[0];

        foreach (var name in names)
        {
            var active = Path.Combine(plugins, name);
            var disabled = active + ".disabled";

            try
            {
                if (name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(active))
                    {
                        // 新包刚放进去的是 active；旧的 .disabled 不应覆盖它。
                        if (File.Exists(disabled))
                        {
                            File.Delete(disabled);
                        }
                    }
                    else if (File.Exists(disabled))
                    {
                        File.Move(disabled, active, true);
                    }
                }
                else if (File.Exists(active))
                {
                    File.Move(active, disabled, true);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>只移除指定加载器的注入文件；mod / 加载器本体目录保留。</summary>
    private static void RemoveInjectionFiles(string gamePath, ModLoaderKind loader)
    {
        var doorstop = Path.Combine(gamePath, "doorstop_config.ini");
        var winhttp = Path.Combine(gamePath, "winhttp.dll");
        var version = Path.Combine(gamePath, "version.dll");

        try
        {
            if (loader == ModLoaderKind.MelonLoader)
            {
                // MelonLoader 的 Windows 注入器。
                if (File.Exists(version))
                {
                    File.Delete(version);
                }
            }
            else if (loader is ModLoaderKind.UnityModManager or ModLoaderKind.BepInEx)
            {
                var target = ReadDoorstopTarget(doorstop);
                if (target is null)
                {
                    return;
                }

                var matches = loader == ModLoaderKind.UnityModManager
                    ? target.Contains("UnityModManager", StringComparison.OrdinalIgnoreCase)
                    : target.Contains("BepInEx", StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    if (File.Exists(winhttp))
                    {
                        File.Delete(winhttp);
                    }

                    if (File.Exists(doorstop))
                    {
                        File.Delete(doorstop);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[loader] 移除 {loader} 注入文件失败：{ex.Message}");
        }
    }

    private static string? ReadDoorstopTarget(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("target_assembly", StringComparison.OrdinalIgnoreCase))
                {
                    var index = trimmed.IndexOf('=');
                    return index >= 0 ? trimmed[(index + 1)..].Trim() : null;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    // ---------------- 下载 / 文件工具 ----------------

    private static async Task<string> DownloadAsync(
        LoaderVersionOption option,
        IProgress<string>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(option.DownloadUrl))
        {
            throw new InvalidOperationException($"版本 {option.Version} 没有下载地址。");
        }

        progress?.Report($"正在下载 {option.Version}…");
        var temp = Path.Combine(Path.GetTempPath(), $"aurora-loader-{Guid.NewGuid():N}.zip");
        await DownloadService.DownloadAsync(Http, option.DownloadUrl, temp, downloadProgress, ct);
        return temp;
    }

    private static void CopyDirectoryContents(string source, string target)
    {
        Directory.CreateDirectory(target);
        var sourceFull = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
        var targetFull = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceFull, file);
            var destination = Path.GetFullPath(Path.Combine(targetFull, relative));
            if (!destination.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void BackupInjectionFiles(string gamePath)
    {
        try
        {
            var key = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gamePath))))[..12];
            var root = Path.Combine(AppPaths.DataDirectory, "loader-backups", key);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(root, stamp);
            Directory.CreateDirectory(target);

            foreach (var name in new[] { "winhttp.dll", "doorstop_config.ini", "version.dll" })
            {
                var source = Path.Combine(gamePath, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(target, name), true);
                }
            }

            File.WriteAllText(Path.Combine(target, "game-path.txt"), gamePath);
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[loader] 备份注入文件失败：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/0.1.8");
        return client;
    }
}



