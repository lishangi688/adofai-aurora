using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 一次扫描得到的“游戏加载器环境”。
///
/// ADOFAI 现在常见三种加载器：
///  · UnityModManager：Doorstop 注入，mod 在 Mods（或 UMMMods）；
///  · MelonLoader：version.dll 注入，mod 在 Mods，插件在 Plugins，依赖在 UserLibs；
///  · BepInEx：Doorstop 注入，插件在 BepInEx/plugins。
///
/// UMMCompat 是 MelonLoader 的插件，它会把 UMM 的 mod 目录重定向到 UMMMods，
/// 从而避免和 MelonLoader 的 Mods 目录冲突。这个类把相关路径一次性算清楚，
/// 后续扫描 / 安装 / 启用禁用都只认这里的结果。
/// </summary>
public sealed class GameLoaderEnvironment
{
    private static readonly Regex MelonVersionPattern = new(@"MelonLoader\s+v?([0-9]+(?:\.[0-9]+)+)", RegexOptions.IgnoreCase);

    public required string GamePath { get; init; }

    public required string ExecutablePath { get; init; }

    public required string DataFolderName { get; init; }

    public required string UmmLoaderPath { get; init; }

    /// <summary>UMM 实际运行目录：普通安装是 Managed/UnityModManager，UMMCompat 是 UserLibs。</summary>
    public required string UmmRuntimePath { get; init; }

    /// <summary>UMM mod 目录（UMMCompat 下是 UMMMods）。</summary>
    public required string UmmModsPath { get; init; }

    public required string UmmParamsPath { get; init; }

    public required string MelonModsPath { get; init; }

    public required string MelonPluginsPath { get; init; }

    public required string MelonUserLibsPath { get; init; }

    public required string BepInExPluginsPath { get; init; }

    public required string BepInExCorePath { get; init; }

    public string? UmmVersion { get; init; }

    public string? MelonLoaderVersion { get; init; }

    public string? BepInExVersion { get; init; }

    public string? UmmCompatVersion { get; init; }

    public bool UmmLoaderInstalled { get; init; }

    public bool UmmInjectionInstalled { get; init; }

    public bool UmmCompatInstalled { get; init; }

    public bool UmmCompatEnabled { get; init; }

    public bool MelonLoaderInstalled { get; init; }

    public bool BepInExInstalled { get; init; }

    public ModLoaderKind ActiveInjection { get; init; }

    /// <summary>普通 Mods 目录当前归谁使用。</summary>
    public ModLoaderKind ModsRootOwner { get; init; }

    public string? DoorstopTarget { get; init; }

    public bool HasAnyLoader =>
        UmmLoaderInstalled || UmmInjectionInstalled || UmmCompatInstalled || MelonLoaderInstalled || BepInExInstalled;

    public bool HasInjection => ActiveInjection is not ModLoaderKind.Unknown;

    /// <summary>普通 Mods 目录是否应被当成 MelonLoader 的目录。</summary>
    public bool UsesMelonMods => ModsRootOwner == ModLoaderKind.MelonLoader;

    public static GameLoaderEnvironment Detect(string gamePath)
    {
        var executable = Path.Combine(gamePath, SteamGameLocator.GameExeName);
        var dataFolder = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName) + "_Data";
        var managed = Path.Combine(gamePath, dataFolder, "Managed");
        var ummLoaderPath = Path.Combine(managed, "UnityModManager");
        var userLibs = Path.Combine(gamePath, "UserLibs");
        var plugins = Path.Combine(gamePath, "Plugins");
        var melonPath = Path.Combine(gamePath, "MelonLoader");
        var bepInExCore = Path.Combine(gamePath, "BepInEx", "core");

        var ummUserLib = Path.Combine(userLibs, "UnityModManager.dll");
        var ummManaged = Path.Combine(ummLoaderPath, "UnityModManager.dll");

        var ummCompatPlugin = FindFile(plugins, "UMMBridge.dll")
                              ?? FindFile(plugins, "UMMCompat.dll");

        var ummCompatInstalled = ummCompatPlugin is not null;
        var ummCompatEnabled = ummCompatPlugin is not null &&
                               !ummCompatPlugin.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var ummBridgeActive = ummCompatEnabled &&
                              Path.GetFileName(ummCompatPlugin!)
                                  .Equals("UMMBridge.dll", StringComparison.OrdinalIgnoreCase);

        var melonInstalled = Directory.Exists(melonPath) ||
                             File.Exists(Path.Combine(melonPath, "MelonLoader.version")) ||
                             File.Exists(Path.Combine(gamePath, "version.dll"));

        var bepInExInstalled = File.Exists(Path.Combine(bepInExCore, "BepInEx.Preloader.dll"));

        var winhttpInstalled = File.Exists(Path.Combine(gamePath, "winhttp.dll"));
        var doorstopTarget = ReadIniValue(Path.Combine(gamePath, "doorstop_config.ini"), "target_assembly");
        var activeInjection = DetectInjection(gamePath, doorstopTarget, winhttpInstalled, melonInstalled);

        // 普通 UMM 注入时以 Managed/UnityModManager 为准；
        // MelonLoader + 启用的 UMMCompat 时以 UserLibs 为准。
        var ummRuntime = activeInjection == ModLoaderKind.UnityModManager && File.Exists(ummManaged)
            ? ummLoaderPath
            : ummBridgeActive && File.Exists(ummManaged)
                ? ummLoaderPath
                : ummCompatEnabled && File.Exists(ummUserLib)
                    ? userLibs
                    : File.Exists(ummManaged)
                        ? ummLoaderPath
                        : userLibs;

        var ummRuntimeDll = Path.Combine(ummRuntime, "UnityModManager.dll");

        var ummInjection = File.Exists(ummManaged) &&
                           activeInjection == ModLoaderKind.UnityModManager;

        var modsRootOwner = DetectModsRootOwner(
            gamePath,
            ummRuntime,
            ummCompatInstalled,
            melonInstalled,
            ummManaged,
            activeInjection);

        var ummConfigPath = Path.Combine(ummRuntime, "Config.xml");
        var ummModsPath = ResolveModsDirectory(gamePath, ummConfigPath, ummCompatEnabled, ummBridgeActive);

        return new GameLoaderEnvironment
        {
            GamePath = gamePath,
            ExecutablePath = executable,
            DataFolderName = dataFolder,
            UmmLoaderPath = ummLoaderPath,
            UmmRuntimePath = ummRuntime,
            UmmModsPath = ummModsPath,
            UmmParamsPath = Path.Combine(ummRuntime, "Params.xml"),
            MelonModsPath = Path.Combine(gamePath, "Mods"),
            MelonPluginsPath = plugins,
            MelonUserLibsPath = userLibs,
            BepInExPluginsPath = Path.Combine(gamePath, "BepInEx", "plugins"),
            BepInExCorePath = bepInExCore,
            UmmVersion = ReadAssemblyVersion(ummRuntimeDll),
            MelonLoaderVersion = ReadMelonVersion(gamePath, melonInstalled),
            BepInExVersion = ReadAssemblyVersion(Path.Combine(bepInExCore, "BepInEx.dll")),
            UmmCompatVersion = ummCompatPlugin is null ? null : ReadAssemblyVersion(ummCompatPlugin),
            UmmLoaderInstalled = File.Exists(ummRuntimeDll),
            UmmInjectionInstalled = ummInjection,
            UmmCompatInstalled = ummCompatInstalled,
            UmmCompatEnabled = ummCompatEnabled,
            MelonLoaderInstalled = melonInstalled,
            BepInExInstalled = bepInExInstalled,
            ActiveInjection = activeInjection,
            ModsRootOwner = modsRootOwner,
            DoorstopTarget = doorstopTarget,
        };
    }

    private static string ResolveModsDirectory(
        string gamePath,
        string configPath,
        bool ummCompatInstalled,
        bool ummBridgeActive)
    {
        var fallback = Path.Combine(gamePath, "Mods");

        // 新版 UMMBridge 在运行时拦截 modsPath 并强制重定向到 UMMMods，
        // 即使它自带的 Config.xml 里仍写着 Mods。
        if (ummBridgeActive)
        {
            return Path.Combine(gamePath, "UMMMods");
        }

        // 旧 UMMCompat 的 Config.xml 会把目录写成 UMMMods。
        var configured = ReadXmlValue(configPath, "ModsDirectory");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim().Trim('"', '\'');
            return Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(gamePath, trimmed);
        }

        // 配置缺失时，UMMCompat 环境按约定使用 UMMMods；普通 UMM 仍用 Mods。
        return ummCompatInstalled ? Path.Combine(gamePath, "UMMMods") : fallback;
    }

    private static ModLoaderKind DetectInjection(
        string gamePath,
        string? doorstopTarget,
        bool winhttpInstalled,
        bool melonInstalled)
    {
        // Doorstop 只有同时存在 winhttp.dll 时才会实际注入。
        if (winhttpInstalled && !string.IsNullOrWhiteSpace(doorstopTarget))
        {
            var normalized = doorstopTarget.Replace('/', '\\');
            if (normalized.Contains("UnityModManager", StringComparison.OrdinalIgnoreCase))
            {
                return ModLoaderKind.UnityModManager;
            }

            if (normalized.Contains("BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                return ModLoaderKind.BepInEx;
            }
        }

        // MelonLoader 在 Windows 上通常使用 version.dll；部分版本也会用 winhttp.dll。
        if (melonInstalled &&
            (File.Exists(Path.Combine(gamePath, "version.dll")) ||
             File.Exists(Path.Combine(gamePath, "winhttp.dll"))))
        {
            return ModLoaderKind.MelonLoader;
        }

        return ModLoaderKind.Unknown;
    }

    private static ModLoaderKind DetectModsRootOwner(
        string gamePath,
        string ummRuntimePath,
        bool ummCompatInstalled,
        bool melonInstalled,
        string ummManagedDll,
        ModLoaderKind activeInjection)
    {
        // 当前实际注入的是谁，Mods 就优先归谁。这样在 MelonLoader 文件还留着、
        // 但已经切回 UMM 注入时，不会把 UMM 的 Mods 误判成 MelonLoader。
        if (activeInjection == ModLoaderKind.UnityModManager)
        {
            return ModLoaderKind.UnityModManager;
        }

        if (activeInjection == ModLoaderKind.MelonLoader)
        {
            return ModLoaderKind.MelonLoader;
        }

        if (ummCompatInstalled || melonInstalled)
        {
            return ModLoaderKind.MelonLoader;
        }

        if (File.Exists(ummManagedDll) || File.Exists(Path.Combine(ummRuntimePath, "UnityModManager.dll")))
        {
            return ModLoaderKind.UnityModManager;
        }

        var mods = Path.Combine(gamePath, "Mods");
        try
        {
            if (Directory.Exists(mods))
            {
                foreach (var dir in Directory.EnumerateDirectories(mods))
                {
                    if (File.Exists(Path.Combine(dir, "Info.json")) ||
                        File.Exists(Path.Combine(dir, "info.json")))
                    {
                        return ModLoaderKind.UnityModManager;
                    }
                }

                if (Directory.EnumerateFiles(mods, "*.dll").Any())
                {
                    return ModLoaderKind.MelonLoader;
                }
            }
        }
        catch
        {
            // 检测失败时按 UMM 处理，保持旧行为
        }

        return ModLoaderKind.UnityModManager;
    }

    private static string? FindFile(string directory, string fileName)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, fileName, SearchOption.TopDirectoryOnly).FirstOrDefault()
                   ?? Directory.EnumerateFiles(directory, fileName + ".disabled", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadIniValue(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                if (line[..index].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(index + 1)..].Trim();
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static string? ReadXmlValue(string path, string elementName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var doc = XDocument.Load(path);
            return doc.Root?.Element(elementName)?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadAssemblyVersion(string path)
    {
        try
        {
            return File.Exists(path)
                ? AssemblyName.GetAssemblyName(path).Version?.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadMelonVersion(string gamePath, bool melonInstalled)
    {
        if (!melonInstalled)
        {
            return null;
        }

        var versionFile = Path.Combine(gamePath, "MelonLoader", "MelonLoader.version");
        try
        {
            if (File.Exists(versionFile))
            {
                return File.ReadAllText(versionFile).Trim();
            }

            var log = Path.Combine(gamePath, "MelonLoader", "Latest.log");
            if (File.Exists(log))
            {
                foreach (var line in File.ReadLines(log).Take(20))
                {
                    var match = MelonVersionPattern.Match(line);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }

        return "未知";
    }
}
