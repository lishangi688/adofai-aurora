using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 本地 mod 管理：扫描各加载器的目录、安装 zip、卸载、启用/禁用。
///
/// 现在一个游戏目录里可能同时存在：
///  · UnityModManager（普通 / UMMCompat）
///  · MelonLoader
///  · BepInEx
/// 因此安装 zip 时会先识别包格式，再决定放到哪个目录。
/// </summary>
public sealed class ModService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] IgnoredMelonDirectories =
    [
        "Resources", "UserData", "UserLibs", "Plugins", "MelonLoader",
        "Libraries", "Dependencies", "Mods", ".git", ".vs",
    ];

    /// <summary>
    /// Plugins 目录里属于「加载器本身」的组件，不作为 mod 列出
    /// （UMMCompat / UMMBridge 由「加载器」页安装和卸载，重复出现会误导用户）。
    /// </summary>
    private static readonly string[] IgnoredMelonPluginFiles =
    [
        "UMMBridge.dll", "UMMCompat.dll",
    ];

    public string GamePath { get; }

    public GameLoaderEnvironment Environment { get; }

    /// <summary>UMM mod 目录。UMMCompat 环境下是 UMMMods，否则通常是 Mods。</summary>
    public string ModsPath => Environment.UmmModsPath;

    public string MelonModsPath => Environment.MelonModsPath;

    public string BepInExPluginsPath => Environment.BepInExPluginsPath;

    public string LoaderPath => Environment.UmmLoaderPath;

    public UmmParamsService Params { get; }

    public bool IsLoaderInstalled => Environment.HasAnyLoader;

    public ModService(string gamePath)
    {
        GamePath = gamePath;
        Environment = GameLoaderEnvironment.Detect(gamePath);
        Params = new UmmParamsService(Environment.UmmParamsPath);
    }

    // ==================================================================
    // 实例 / 扫描结果缓存（切换页面时的卡顿主要来自这里）
    // ==================================================================

    private static readonly object CacheLock = new();

    private static string? _cachedPath;

    private static ModService? _cachedService;

    /// <summary>
    /// 取一个复用的 <see cref="ModService"/>。
    ///
    /// 构造它要跑一遍 <see cref="GameLoaderEnvironment.Detect"/>：几十次文件存在性判断、
    /// 还要读几个程序集版本、翻MelonLoader 日志。原来每个页面、每次刷新都新建一个，
    /// 切一次列表就白跑一遍，UI 线程直接被卡住。
    /// 任何会改变磁盘状态的操作（安装 / 卸载 / 启停 / 切换加载器）后要调用 <see cref="InvalidateCache"/>。
    /// </summary>
    public static ModService Get(string gamePath)
    {
        lock (CacheLock)
        {
            if (_cachedService is not null &&
                string.Equals(_cachedPath, gamePath, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedService;
            }

            _cachedService = new ModService(gamePath);
            _cachedPath = gamePath;
            return _cachedService;
        }
    }

    /// <summary>
    /// 复用缓存的加载器环境探测结果（和 <see cref="Get"/> 用同一个实例）。
    /// 页面里凡是 GameLoaderEnvironment.Detect(...) 的地方都该走这里，否则每次刷新都重探一遍磁盘。
    /// </summary>
    public static GameLoaderEnvironment DetectEnvironment(string gamePath) => Get(gamePath).Environment;

    /// <summary>丢掉缓存的实例与扫描结果（改了游戏目录、装卸完 mod / 加载器后调用）。</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedService?.InvalidateScan();
            _cachedService = null;
            _cachedPath = null;
        }
    }

    /// <summary>丢弃本实例的扫描结果（磁盘上刚装/删过东西时调用）。</summary>
    public void InvalidateScan()
    {
        lock (_scanLock)
        {
            _scanCache = null;
            _scanTime = DateTime.MinValue;
        }
    }

    // ==================================================================
    // 扫描
    // ==================================================================

    /// <summary>扫描结果的短暂缓存：切换页面 / 刷新列表时不必每次都重读一遍磁盘与程序集元数据。</summary>
    private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(3);

    private readonly object _scanLock = new();

    private IReadOnlyList<InstalledMod>? _scanCache;

    private DateTime _scanTime = DateTime.MinValue;

    public IReadOnlyList<InstalledMod> Scan()
    {
        lock (_scanLock)
        {
            if (_scanCache is not null && DateTime.UtcNow - _scanTime < ScanCacheTtl)
            {
                return _scanCache;
            }

            _scanCache = ScanCore();
            _scanTime = DateTime.UtcNow;
            return _scanCache;
        }
    }

    private IReadOnlyList<InstalledMod> ScanCore()
    {
        var result = new List<InstalledMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(InstalledMod mod)
        {
            var key = $"{mod.Loader}|{Path.GetFullPath(mod.FolderPath)}";
            if (seen.Add(key))
            {
                result.Add(mod);
            }
        }

        // UMM 目录永远扫描；如果当前是普通 UMM，它就是 Mods。
        // 但如果 Mods 已经归 MelonLoader 使用，且 UMM 没有独立目录（没有 UMMCompat），
        // 就不要把同一个 Mods 再按 UMM 扫一遍。
        var sameModsRoot = string.Equals(
            Path.GetFullPath(Environment.UmmModsPath).TrimEnd('\\', '/'),
            Path.GetFullPath(Environment.MelonModsPath).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

        if (!(sameModsRoot && Environment.UsesMelonMods))
        {
            ScanUmm(Environment.UmmModsPath, Add);
        }

        // 从 UMM 切换到 MelonLoader 后，原 UMM mod 可能停放在 UMMMods；
        // 即使当前没有 UMMCompat，也让它们出现在「已安装」里，避免用户以为 mod 丢了。
        var parkedUmmMods = Path.Combine(GamePath, "UMMMods");
        if (!string.Equals(
                Path.GetFullPath(parkedUmmMods).TrimEnd('\\', '/'),
                Path.GetFullPath(Environment.UmmModsPath).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            ScanUmm(parkedUmmMods, Add);
        }

        // Mods 目录只在归 MelonLoader 使用时才按 ML 扫描，避免把 UMM 的 Info.json
        // 重复算成两份。如果 UMM 已经使用独立的 UMMMods（例如切回 UMM 但 ML 还没卸载），
        // 那么 Mods 仍然可以按 MelonLoader 列出。
        if (Environment.UsesMelonMods ||
            (Environment.MelonLoaderInstalled && !sameModsRoot))
        {
            ScanMelon(Environment.MelonModsPath, Add);
        }

        // MelonLoader 除了 Mods 还有 Plugins（MelonPlugin 插件目录）。
        // 之前只扫 Mods，装在 Plugins 下的插件整个漏掉，界面上既看不到也打不开。
        if (Environment.MelonLoaderInstalled || Directory.Exists(Environment.MelonPluginsPath))
        {
            ScanMelon(Environment.MelonPluginsPath, Add, IgnoredMelonPluginFiles);
        }

        if (Environment.BepInExInstalled || Directory.Exists(Environment.BepInExPluginsPath))
        {
            ScanBepInEx(Environment.BepInExPluginsPath, Add);
        }

        result.Sort((a, b) =>
        {
            var loader = a.Loader.CompareTo(b.Loader);
            return loader != 0
                ? loader
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });

        return result;
    }

    private void ScanUmm(string root, Action<InstalledMod> add)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var infoPath = FindInfoJson(dir);
            if (infoPath is null)
            {
                continue;
            }

            try
            {
                var info = JsonSerializer.Deserialize<ModInfoJson>(File.ReadAllText(infoPath), JsonOptions);
                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    AppPaths.AppendDebugLog($"[scan] UMM Info.json 缺少 Id：{infoPath}");
                    continue;
                }

                add(new InstalledMod
                {
                    Id = info.Id,
                    Loader = ModLoaderKind.UnityModManager,
                    DisplayName = string.IsNullOrWhiteSpace(info.DisplayName) ? info.Id : info.DisplayName,
                    Version = info.Version,
                    Author = info.Author,
                    GameVersion = info.GameVersion,
                    ManagerVersion = info.ManagerVersion,
                    Requirements = info.Requirements,
                    HomePage = info.HomePage,
                    Repository = info.Repository,
                    FolderPath = dir,
                    FolderName = Path.GetFileName(dir),
                    IsEnabled = Params.IsEnabled(info.Id),
                });
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[scan] 无法解析 UMM mod：{infoPath}：{ex.Message}");
            }
        }
    }

    private void ScanMelon(string root, Action<InstalledMod> add, IReadOnlyCollection<string>? ignoredFileNames = null)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name) ||
                IgnoredMelonDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith('.'))
            {
                continue;
            }

            try
            {
                var dlls = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(dir, "*.dll.disabled", SearchOption.AllDirectories))
                    .ToList();

                var manifest = FindMelonManifest(dir);
                if (dlls.Count == 0 && manifest is null)
                {
                    continue;
                }

                var manifestInfo = ReadMelonManifest(manifest);
                var version = manifestInfo.Version ?? ReadAssemblyVersion(FindPrimaryDll(dlls));
                var enabled = dlls.Any(d => !d.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));

                add(new InstalledMod
                {
                    Id = manifestInfo.Id ?? name,
                    Loader = ModLoaderKind.MelonLoader,
                    DisplayName = manifestInfo.Name ?? name,
                    Version = version,
                    Author = manifestInfo.Author,
                    Repository = manifestInfo.Repository,
                    HomePage = manifestInfo.HomePage,
                    FolderPath = dir,
                    FolderName = name,
                    IsEnabled = enabled,
                });
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[scan] 无法解析 MelonLoader mod：{dir}：{ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(root, "*.dll.disabled", SearchOption.TopDirectoryOnly)))
        {
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var displayName = Path.GetFileName(file);
            if (isDisabled)
            {
                displayName = displayName[..^".disabled".Length];
            }

            // 加载器自身的组件（例如 Plugins\UMMBridge.dll）不作为 mod 列出。
            if (ignoredFileNames?.Contains(displayName, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            var id = Path.GetFileNameWithoutExtension(displayName);
            add(new InstalledMod
            {
                Id = id,
                Loader = ModLoaderKind.MelonLoader,
                DisplayName = id,
                Version = ReadAssemblyVersion(file),
                FolderPath = file,
                FolderName = Path.GetFileName(file),
                IsEnabled = !isDisabled,
            });
        }
    }

    private void ScanBepInEx(string root, Action<InstalledMod> add)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
            {
                continue;
            }

            try
            {
                var dlls = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(dir, "*.dll.disabled", SearchOption.AllDirectories))
                    .ToList();

                var manifest = FindMelonManifest(dir);
                if (dlls.Count == 0 && manifest is null)
                {
                    continue;
                }

                var manifestInfo = ReadMelonManifest(manifest);
                add(new InstalledMod
                {
                    Id = manifestInfo.Id ?? name,
                    Loader = ModLoaderKind.BepInEx,
                    DisplayName = manifestInfo.Name ?? name,
                    Version = manifestInfo.Version ?? ReadAssemblyVersion(FindPrimaryDll(dlls)),
                    Author = manifestInfo.Author,
                    Repository = manifestInfo.Repository,
                    HomePage = manifestInfo.HomePage,
                    FolderPath = dir,
                    FolderName = name,
                    IsEnabled = dlls.Any(d => !d.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)),
                });
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[scan] 无法解析 BepInEx 插件：{dir}：{ex.Message}");
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(root, "*.dll.disabled", SearchOption.TopDirectoryOnly)))
        {
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var displayName = Path.GetFileName(file);
            if (isDisabled)
            {
                displayName = displayName[..^".disabled".Length];
            }

            var id = Path.GetFileNameWithoutExtension(displayName);
            add(new InstalledMod
            {
                Id = id,
                Loader = ModLoaderKind.BepInEx,
                DisplayName = id,
                Version = ReadAssemblyVersion(file),
                FolderPath = file,
                FolderName = Path.GetFileName(file),
                IsEnabled = !isDisabled,
            });
        }
    }

    private static string? FindInfoJson(string directory)
    {
        var path = Path.Combine(directory, "Info.json");
        if (File.Exists(path))
        {
            return path;
        }

        path = Path.Combine(directory, "info.json");
        return File.Exists(path) ? path : null;
    }

    private static string? FindMelonManifest(string directory)
    {
        foreach (var name in new[] { "manifest.json", "ModInfo.json", "modinfo.json", "Info.json", "info.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static MelonManifestInfo ReadMelonManifest(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // MelonLoader 的 ModInfo.json 里有一个 DownloadedFrom（作者填的来源地址），
            // 有它就能自动认出 GitHub / Repository.json 更新源，MelonLoader mod 也能自动检查更新。
            var url = GetString(root, "DownloadedFrom", "Homepage", "HomePage", "Repository", "SourceUrl");

            return new MelonManifestInfo(
                GetString(root, "id", "Id", "name", "Name"),
                GetString(root, "name", "Name", "displayName", "DisplayName"),
                GetString(root, "version", "Version"),
                GetString(root, "author", "Author"),
                Repository: url is not null && url.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? url : null,
                HomePage: url is not null && !url.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? url : null);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>MelonLoader / BepInEx 的清单文件读出来的东西。</summary>
    private readonly record struct MelonManifestInfo(
        string? Id,
        string? Name,
        string? Version,
        string? Author,
        string? Repository = null,
        string? HomePage = null);

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string? FindPrimaryDll(IEnumerable<string> files)
    {
        return files
            .Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault()
            ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    private static string? ReadAssemblyVersion(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            return AssemblyName.GetAssemblyName(file).Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // 安装 zip
    // ==================================================================

    /// <param name="zipPath">mod 压缩包。</param>
    /// <param name="target">
    /// 更新时传入「当前已安装的那个 mod」。同一个包经常同时带 UMM / MelonLoader / BepInEx 多个变体，
    /// 传入它就能保证"当初装在哪、就按哪种方式更新在哪"，不会因为包内容或环境变化把 mod 挪到别处。
    /// 传null = 全新安装，按包内容自动识别。
    /// </param>
    public InstallResult InstallFromZip(string zipPath, InstalledMod? target = null)
    {
        if (!File.Exists(zipPath))
        {
            return new InstallResult(false, "文件不存在。");
        }

        // 装完磁盘就变了：本实例的扫描结果立刻作废（别的页面再切回来时能立刻看到新版本）。
        InvalidateScan();

        if (GameProcessGuard.IsGameRunning())
        {
            return new InstallResult(false, "游戏正在运行。请先退出游戏，再安装或更新 mod。");
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => new PackageEntry(e, NormalizeEntryPath(e.FullName)))
                .Where(e => e.Path.Length > 0)
                .ToList();

            if (entries.Count == 0)
            {
                return new InstallResult(false, "压缩包是空的。");
            }

            // UMMCompat / UMMBridge 是 MelonLoader 的兼容插件包，先认出来。
            if (IsUmmCompatPackage(entries))
            {
                return InstallUmmCompat(entries);
            }

            // 更新（而不是首次安装）：沿用当前 mod 的加载器与安装方式。
            // 一个包经常同时带 UMM / MelonLoader / BepInEx 三个变体，如果在这里重新自动识别，
            // 结果很可能与当初安装的位置不一致（例如 UMM 的 mod 被"更新"进了 Mods 目录）。
            if (target is { } hint &&
                hint.Loader is ModLoaderKind.UnityModManager or ModLoaderKind.MelonLoader or ModLoaderKind.BepInEx)
            {
                return InstallAsInstalledLoader(entries, hint);
            }

            var infoEntries = entries
                .Where(e => Path.GetFileName(e.Path).Equals("Info.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path.Count(c => c == '/'))
                .ToList();

            if (infoEntries.Count > 0)
            {
                var info = ReadInfo(infoEntries[0]);
                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    return new InstallResult(false, "Info.json 无效或缺少 Id 字段。");
                }

                return InstallUmm(entries, info, infoEntries[0], infoEntries.Count > 1);
            }

            // 路径不一定可靠：很多 BepInEx / MelonLoader 包是“扁平” DLL，
            // 或者把多个加载器的变体放在同一个包里。直接读取 DLL 元数据确认。
            var dllFlags = InspectPackageDlls(entries);
            var pathBep = IsBepInExPackage(entries);
            var pathMelon = HasMelonStructure(entries);
            var isBep = pathBep || dllFlags.BepInEx;
            var isMelon = pathMelon || dllFlags.MelonLoader;

            if (isBep && !isMelon)
            {
                return InstallBepInEx(entries);
            }

            if (isMelon && !isBep)
            {
                return InstallMelon(entries);
            }

            if (isBep && isMelon)
            {
                // 同时包含两种加载器时，按当前环境和目录结构选一个，避免装错。
                if (Environment.ActiveInjection == ModLoaderKind.BepInEx ||
                    (Environment.BepInExInstalled && !Environment.MelonLoaderInstalled))
                {
                    return InstallBepInEx(entries);
                }

                if (Environment.MelonLoaderInstalled || Environment.UmmCompatEnabled)
                {
                    return InstallMelon(entries);
                }

                if (pathBep && !pathMelon)
                {
                    return InstallBepInEx(entries);
                }

                if (pathMelon && !pathBep)
                {
                    return InstallMelon(entries);
                }

                return InstallMelon(entries);
            }

            if (entries.Any(e => e.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                return InstallMelon(entries);
            }

            return new InstallResult(
                false,
                "无法识别这个压缩包：没有 Info.json，也没有可加载的 DLL。可能不是 UMM / MelonLoader / BepInEx 的 mod 包。");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按"当前这个 mod 当初是怎么装的"来更新：不重新猜包格式，只认它所属的加载器。
    ///
    /// 这解决了同一个 mod 支持多个加载器时的两个问题：
    ///  ① 装了 MelonLoader 版，却在更新时被 Info.json 带去装成 UMM 版；
    ///  ② 一个包里 MelonLoader 与 BepInEx 的 DLL 混在一起，装到另一个加载器的目录里去。
    /// </summary>
    private InstallResult InstallAsInstalledLoader(IReadOnlyList<PackageEntry> entries, InstalledMod target)
    {
        switch (target.Loader)
        {
            case ModLoaderKind.UnityModManager:
            {
                var infoEntries = entries
                    .Where(e => Path.GetFileName(e.Path).Equals("Info.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Path.Count(c => c == '/'))
                    .ToList();

                if (infoEntries.Count == 0)
                {
                    return new InstallResult(
                        false,
                        $"「{target.DisplayName}」装在 UMM 目录，但新版本压缩包里没有 Info.json，无法按 UMM 方式更新。");
                }

                var info = ReadInfo(infoEntries[0]);
                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    return new InstallResult(false, "Info.json 无效或缺少 Id 字段。");
                }

                return InstallUmm(entries, info, infoEntries[0], infoEntries.Count > 1, target);
            }

            case ModLoaderKind.MelonLoader:
                return InstallMelon(entries, target);

            case ModLoaderKind.BepInEx:
                return InstallBepInEx(entries, target);

            default:
                return new InstallResult(false, $"无法识别「{target.LoaderLabel}」的安装方式。");
        }
    }

    private InstallResult InstallUmm(
        IReadOnlyList<PackageEntry> entries,
        ModInfoJson info,
        PackageEntry infoEntry,
        bool multipleMods,
        InstalledMod? target = null)
    {
        if (!TryValidateId(info.Id, out var safeId, out var idError))
        {
            return new InstallResult(false, idError);
        }

        var root = Environment.UmmModsPath;
        Directory.CreateDirectory(root);

        var prefix = GetDirectoryPrefix(infoEntry.Path);
        var staging = Path.Combine(root, ".aurora-staging", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);
            var extracted = ExtractEntries(entries, staging, prefix, requireInfoJson: true);
            if (extracted == 0)
            {
                return new InstallResult(false, "压缩包里没有可安装的文件（目录结构可能不规范）。");
            }

            var targetDir = Path.GetFullPath(Path.Combine(root, safeId));
            var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!targetDir.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult(false, "Info.json 里的 Id 不能用作目录名。");
            }

            var oldFolders = FindFoldersWithModId(root, info.Id);

            // 更新场景：把"当前正在更新的那个文件夹"也算作要替换的旧目录。
            // 旧文件夹可能因为站长改过 Id 而和Info.json 对不上（例如 out 与 AccurateJudgementBar），
            // 漏掉它会装出第二份，界面上就出现"同一个 mod 有两份"的重复警告。
            if (target is { FolderPath.Length: > 0 } hint &&
                Directory.Exists(hint.FolderPath) &&
                !oldFolders.Any(f => string.Equals(f, hint.FolderPath, StringComparison.OrdinalIgnoreCase)))
            {
                var hintFull = Path.GetFullPath(hint.FolderPath);
                if (hintFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    oldFolders.Add(hintFull);
                }
            }

            var previousSettings = TryReadPreviousSettings(oldFolders);

            // 先把旧目录移动到备份位置，再放新版本；失败时恢复。
            var backupRoot = Path.Combine(root, ".aurora-backup", Guid.NewGuid().ToString("N"));
            var moved = new List<(string From, string To)>();
            try
            {
                if (oldFolders.Count > 0)
                {
                    Directory.CreateDirectory(backupRoot);
                    foreach (var folder in oldFolders)
                    {
                        var destination = Path.Combine(backupRoot, Path.GetFileName(folder));
                        if (Directory.Exists(destination))
                        {
                            destination += "-" + Guid.NewGuid().ToString("N")[..6];
                        }

                        Directory.Move(folder, destination);
                        moved.Add((folder, destination));
                    }
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }

                Directory.Move(staging, targetDir);
            }
            catch
            {
                // 回滚：删掉可能已经放进去的新目录，把旧目录挪回来
                try
                {
                    if (Directory.Exists(targetDir))
                    {
                        Directory.Delete(targetDir, true);
                    }
                }
                catch
                {
                    // 忽略
                }

                foreach (var (from, to) in moved)
                {
                    try
                    {
                        if (Directory.Exists(to) && !Directory.Exists(from))
                        {
                            Directory.Move(to, from);
                        }
                    }
                    catch
                    {
                        // 忽略
                    }
                }

                throw;
            }

            if (previousSettings is not null &&
                !File.Exists(Path.Combine(targetDir, "Settings.xml")))
            {
                try
                {
                    File.WriteAllBytes(Path.Combine(targetDir, "Settings.xml"), previousSettings);
                }
                catch
                {
                    // 忽略
                }
            }

            TryDeleteDirectory(backupRoot);

            var restored = oldFolders.Count > 1
                ? $"（已合并 {oldFolders.Count - 1} 个重复文件夹）"
                : string.Empty;
            var multi = multipleMods
                ? "（这个包里还有其它 Info.json，本次只安装了最外层的一个）"
                : string.Empty;
            var warning = Environment.UmmLoaderInstalled || Environment.UmmCompatEnabled
                ? string.Empty
                : " 当前没有安装 UnityModManager，请先到「加载器」页安装。";

            return new InstallResult(
                true,
                $"已安装 {info.DisplayName ?? info.Id} {info.Version} 到 {Environment.UmmModsPath}。{restored}{multi}{warning}",
                info.Id);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装 UMM mod 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(Path.Combine(root, ".aurora-staging"));
        }
    }

    private InstallResult InstallMelon(IReadOnlyList<PackageEntry> entries, InstalledMod? target = null)
    {
        var gameRoot = Path.GetFullPath(GamePath);
        var installEntries = FilterEntriesForLoader(entries, ModLoaderKind.MelonLoader);
        var hasStructured = installEntries.Any(e =>
            e.Path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("UserLibs/", StringComparison.OrdinalIgnoreCase));

        // 更新：沿用当前的安装位置（Mods 下的同名文件夹，或 Plugins 下的 MelonPlugin）
        if (TryGetUpdatePath(
                target,
                [Environment.MelonModsPath, Environment.MelonPluginsPath],
                out var updatePath))
        {
            return UpdateLoaderMod(installEntries, ModLoaderKind.MelonLoader, target!, updatePath, gameRoot);
        }

        var targetRoot = hasStructured ? gameRoot : Path.Combine(gameRoot, "Mods");
        Directory.CreateDirectory(targetRoot);

        var staging = Path.Combine(Path.GetTempPath(), $"aurora-ml-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);

            // 先解压到临时目录，确认确实有 DLL 再写入游戏目录。
            var extracted = ExtractEntries(installEntries, staging, prefix: null, requireInfoJson: false);
            var dllCount = Directory.Exists(staging)
                ? Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).Count()
                : 0;
            if (extracted == 0 || dllCount == 0)
            {
                return new InstallResult(false, "这个压缩包里没有找到 DLL，无法作为 MelonLoader mod 安装。");
            }

            CopyDirectoryContents(staging, targetRoot, overwrite: true);
            var warning = Environment.MelonLoaderInstalled
                ? string.Empty
                : " 当前没有安装 MelonLoader，请先到「加载器」页安装，否则游戏不会加载这个 mod。";
            return new InstallResult(true, $"已安装到 MelonLoader 目录：{targetRoot}。{warning}");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装 MelonLoader mod 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private InstallResult InstallBepInEx(IReadOnlyList<PackageEntry> entries, InstalledMod? target = null)
    {
        var gameRoot = Path.GetFullPath(GamePath);
        var installEntries = FilterEntriesForLoader(entries, ModLoaderKind.BepInEx);
        var hasGameStructure = installEntries.Any(e => e.Path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase));
        var hasPluginsPrefix = installEntries.Any(e => e.Path.StartsWith("plugins/", StringComparison.Ordinal));

        // 更新：沿用当前的安装位置（BepInEx/plugins 下的同名文件夹或单个 dll）
        if (TryGetUpdatePath(target, [Environment.BepInExPluginsPath], out var updatePath))
        {
            return UpdateLoaderMod(installEntries, ModLoaderKind.BepInEx, target!, updatePath, gameRoot);
        }

        var targetRoot = hasGameStructure
            ? gameRoot
            : Environment.BepInExPluginsPath;

        Directory.CreateDirectory(targetRoot);

        var staging = Path.Combine(Path.GetTempPath(), $"aurora-bep-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);

            var extracted = ExtractEntries(
                installEntries,
                staging,
                prefix: hasPluginsPrefix ? "plugins/" : null,
                requireInfoJson: false);

            var dllCount = Directory.Exists(staging)
                ? Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories).Count()
                : 0;
            if (extracted == 0 || dllCount == 0)
            {
                return new InstallResult(false, "这个压缩包里没有找到 DLL，无法作为 BepInEx 插件安装。");
            }

            CopyDirectoryContents(staging, targetRoot, overwrite: true);
            var warning = Environment.BepInExInstalled
                ? string.Empty
                : " 当前没有安装 BepInEx，请先到「加载器」页安装，否则游戏不会加载这个插件。";
            return new InstallResult(
                true,
                (hasGameStructure
                    ? $"已安装到游戏目录（BepInEx 包）：{gameRoot}"
                    : $"已安装到 BepInEx/plugins：{targetRoot}") + warning);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装 BepInEx 插件失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // ==================================================================
    // 更新（MelonLoader / BepInEx）：装在哪就更新在哪
    // ==================================================================

    /// <summary>包里所有文件都放在同一个顶层目录里（Mods/ · Plugins/ · plugins/ · BepInEx/ …）时，那个目录名。</summary>
    private static readonly string[] LoaderContainers = ["Mods", "Plugins", "UserLibs", "BepInEx", "plugins"];

    /// <summary>
    /// 判断能不能按"原地更新"处理：目标必须是这个已安装 mod 自己的文件/文件夹，
    /// 而且要落在对应加载器的目录里（避免误删 UMM 目录或别的东西）。
    /// </summary>
    private static bool TryGetUpdatePath(InstalledMod? target, string[] loaderRoots, out string path)
    {
        path = string.Empty;

        if (target is null || string.IsNullOrWhiteSpace(target.FolderPath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(target.FolderPath);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return false;
        }

        foreach (var root in loaderRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 更新一个已经装好的 MelonLoader / BepInEx mod。
    ///
    /// 规则很简单：当初装在哪，现在就更新到哪。
    ///  · 旧的是文件夹 → 整个替换，新版本没有的旧文件不会残留；
    ///  · 旧的是单个 dll → 新文件放进它所在的目录，旧 dll 被替换掉；
    ///  · 同一个包里 UMM / MelonLoader / BepInEx 三个变体都有 → 只取当前加载器那一份；
    ///  · 之前是禁用状态（*.dll.disabled）→ 更新后仍然保持禁用。
    /// 中途失败会把旧文件/目录放回去。
    /// </summary>
    private InstallResult UpdateLoaderMod(
        IReadOnlyList<PackageEntry> entries,
        ModLoaderKind loader,
        InstalledMod target,
        string existingPath,
        string gameRoot)
    {
        var label = LoaderLabel(loader);
        var isFile = File.Exists(existingPath);
        var role = DetectInstallRole(existingPath);
        var staging = Path.Combine(Path.GetTempPath(), $"aurora-update-{Guid.NewGuid():N}");

        try
        {
            // ① 先按加载器过滤；② 多加载器混合包再去掉 UMM 那一版；③ 把容器前缀映射到当前安装位置。
            var installEntries = FilterEntriesForLoader(entries, loader);
            var hasStructure = installEntries.Any(IsContainerPath);
            if (hasStructure)
            {
                installEntries = DropUmmVariant(installEntries);
            }

            var container = DetectContainer(installEntries);

            // 容器目录（如 Mods/）要映射到"这个 mod 现在所在的那个目录"，
            // 而不是简单地塞回游戏根目录 —— 装在 Plugins 的插件不该被更新到 Mods 去。
            var extractRoot = container is null ? null : ResolveContainerRoot(container, role, loader, gameRoot);
            var mapped = container is not null
                ? MapToContainer(installEntries, container)
                : MapToInstalledFolder(installEntries, role);

            if (mapped.Count == 0)
            {
                return new InstallResult(false, $"新版本压缩包里没有找到 {label} 版本的文件。");
            }

            Directory.CreateDirectory(staging);

            // 先解压到临时目录，确认确实有 DLL 再动游戏目录。
            var extracted = ExtractEntries(mapped, staging, requireInfoJson: false);
            if (extracted == 0 || CountDlls(staging) == 0)
            {
                return new InstallResult(false, $"新版本压缩包里没有找到 DLL，无法更新 {label} mod。");
            }

            // 结构化包：按容器目录写入（可能是游戏根目录，也可能是当前的 Mods / Plugins）
            if (extractRoot is not null)
            {
                var backup = MoveAside(existingPath);
                try
                {
                    CopyDirectoryContents(staging, extractRoot, overwrite: true);
                }
                catch (Exception ex)
                {
                    RestoreAside(backup, existingPath, discard: false);
                    AppPaths.AppendDebugLog($"[update] 写入 {extractRoot} 失败：{ex.Message}");
                    throw;
                }

                RestoreAside(backup, existingPath, discard: true);

                if (!target.IsEnabled && Directory.Exists(existingPath))
                {
                    SetDllEnabled(existingPath, false);
                }

                return new InstallResult(true, $"已更新 {target.DisplayName}（{label}）：{extractRoot}。");
            }

            var parent = Path.GetDirectoryName(existingPath);
            if (isFile)
            {
                // 单个 dll 的 mod：新文件放进同一个目录，旧的 dll 由"挪走"这一步清掉。
                if (string.IsNullOrWhiteSpace(parent))
                {
                    return new InstallResult(false, "无法确定这个 mod 的安装位置。");
                }

                var dlls = RelativeDllPaths(staging);
                var backup = MoveAside(existingPath);
                try
                {
                    CopyDirectoryContents(staging, parent, overwrite: true);
                }
                catch (Exception ex)
                {
                    RestoreAside(backup, existingPath, discard: false);
                    AppPaths.AppendDebugLog($"[update] 写入 {parent} 失败：{ex.Message}");
                    throw;
                }

                RestoreAside(backup, existingPath, discard: true);

                if (!target.IsEnabled)
                {
                    foreach (var relative in dlls)
                    {
                        SetDllEnabled(Path.Combine(parent, relative), false);
                    }
                }

                return new InstallResult(true, $"已更新 {target.DisplayName}（{label}）：{parent}。");
            }

            // 扁平包：包里只有一个 mod 文件夹时把那个文件夹当作 mod 本体，
            // 否则包里的东西本身就是 mod 内容，直接铺进原来的文件夹。
            var payload = ResolveSingleFolder(staging) ?? staging;
            ReplaceDirectory(payload, existingPath);
            SetDllEnabled(existingPath, target.IsEnabled);

            return new InstallResult(true, $"已更新 {target.DisplayName}（{label}）：{existingPath}。");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"更新 {label} mod 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>这个 mod 现在装在哪种容器目录里（Mods / Plugins / BepInEx 的 plugins）。</summary>
    private string? DetectInstallRole(string existingPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(existingPath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        if (IsSameDirectory(parent, Environment.MelonModsPath))
        {
            return "Mods";
        }

        if (IsSameDirectory(parent, Environment.MelonPluginsPath))
        {
            return "Plugins";
        }

        if (IsSameDirectory(parent, Environment.BepInExPluginsPath))
        {
            return "plugins";
        }

        return null;
    }

    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>包里所有文件是否都在同一个顶层容器目录（Mods/ · plugins/ …）里，是则返回该目录名。</summary>
    private static string? DetectContainer(IReadOnlyList<PackageEntry> entries)
    {
        string? container = null;

        foreach (var entry in entries)
        {
            var slash = entry.Path.IndexOf('/');
            if (slash <= 0)
            {
                // 有文件直接躺在压缩包根目录 → 不算"整体装在某个容器里"
                return null;
            }

            var first = entry.Path[..slash];
            if (!LoaderContainers.Contains(first, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            if (container is null)
            {
                container = first;
            }
            else if (!container.Equals(first, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return container;
    }

    private static bool IsContainerPath(PackageEntry entry)
    {
        var slash = entry.Path.IndexOf('/');
        return slash > 0 && LoaderContainers.Contains(entry.Path[..slash], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 多加载器混合包在更新 MelonLoader / BepInEx mod 时，要先把"UMM 那一版"剔掉。
    /// 认法：那棵子目录里有 UMM 版的 Info.json（带 Id / AssemblyName）。
    /// </summary>
    private static IReadOnlyList<PackageEntry> DropUmmVariant(IReadOnlyList<PackageEntry> entries)
    {
        var ummRoots = new List<string>();
        var rootLevelInfo = new List<PackageEntry>();

        foreach (var entry in entries)
        {
            if (!Path.GetFileName(entry.Path).Equals("Info.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slash = entry.Path.LastIndexOf('/');
            var prefix = slash < 0 ? string.Empty : entry.Path[..(slash + 1)];
            if (prefix.Length == 0)
            {
                rootLevelInfo.Add(entry);
                continue;
            }

            var info = ReadInfo(entry);
            if (info is not null &&
                (!string.IsNullOrWhiteSpace(info.Id) || !string.IsNullOrWhiteSpace(info.AssemblyName)))
            {
                ummRoots.Add(prefix);
            }
        }

        if (ummRoots.Count == 0 && rootLevelInfo.Count == 0)
        {
            return entries;
        }

        return entries
            .Where(e => !rootLevelInfo.Contains(e))
            .Where(e => !ummRoots.Any(prefix =>
                e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>容器目录对应的写入位置：优先当前安装位置，其次按包结构推导。</summary>
    private string? ResolveContainerRoot(string container, string? role, ModLoaderKind loader, string gameRoot)
    {
        // 当前就装在这个容器里 → 写回同一个目录（保持"装在哪"）。
        if (role is not null && container.Equals(role, StringComparison.OrdinalIgnoreCase))
        {
            return RoleRoot(role);
        }

        // Plugins（大写）与 plugins（小写）只差大小写，却分别属于 MelonLoader 和 BepInEx，
        // 所以这里必须结合"这个 mod 属于哪个加载器"来判断，不能只看大小写。
        if (loader == ModLoaderKind.MelonLoader && container.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.MelonPluginsPath;
        }

        if (loader == ModLoaderKind.BepInEx && container.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.BepInExPluginsPath;
        }

        if (container.Equals("plugins", StringComparison.Ordinal))
        {
            // BepInEx 的约定是小写 plugins/，指的就是 BepInEx\plugins 本身。
            return Environment.BepInExPluginsPath;
        }

        return gameRoot;
    }

    private string? RoleRoot(string role) => role switch
    {
        "Mods" => Environment.MelonModsPath,
        "Plugins" => Environment.MelonPluginsPath,
        _ => Environment.BepInExPluginsPath,
    };

    /// <summary>剥掉容器前缀，让内容相对容器目录展开。</summary>
    private static IReadOnlyList<PackageEntry> MapToContainer(
        IReadOnlyList<PackageEntry> entries,
        string container)
    {
        var prefix = container + "/";
        return entries
            .Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(e => e with { Path = e.Path[prefix.Length..] })
            .Where(e => e.Path.Length > 0)
            .ToList();
    }

    /// <summary>
    /// 扁平包没有容器前缀：属于别的容器的文件丢掉，剩下的按"当前安装位置"重新映射。
    /// （例如包根目录下的 UMM 版 Info.json 在更新 MelonLoader mod 时不该被写进去。）
    /// </summary>
    private static IReadOnlyList<PackageEntry> MapToInstalledFolder(
        IReadOnlyList<PackageEntry> entries,
        string? role)
    {
        var prefix = role is null ? null : role + "/";

        var result = new List<PackageEntry>();
        foreach (var entry in entries)
        {
            if (IsContainerPath(entry))
            {
                // 属于别的容器（Plugins/ · UserLibs/ …），当前 mod 不在那儿，跳过
                if (prefix is null ||
                    !entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(entry with { Path = entry.Path[prefix.Length..] });
                continue;
            }

            result.Add(entry);
        }

        return result.Where(e => e.Path.Length > 0).ToList();
    }

    /// <summary>包里只有一个顶层文件夹时返回它（很多 mod 包都长这样），否则返回 null。</summary>
    private static string? ResolveSingleFolder(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var items = Directory.GetFileSystemEntries(root);
        return items.Length == 1 && Directory.Exists(items[0]) ? items[0] : null;
    }

    private static int CountDlls(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories).Count()
            : 0;

    /// <summary>暂存目录里所有 DLL 相对于暂存根的路径（用来恢复禁用状态）。</summary>
    private static List<string> RelativeDllPaths(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            result.Add(Path.GetRelativePath(rootFull, file));
        }

        return result;
    }

    /// <summary>用 source 的内容整体替换 destination（先备份旧目录，失败自动还原）。</summary>
    private static void ReplaceDirectory(string source, string destination)
    {
        var backup = MoveAside(destination);
        try
        {
            CopyDirectoryContents(source, destination, overwrite: true);
        }
        catch
        {
            RestoreAside(backup, destination, discard: false);
            throw;
        }
        finally
        {
            RestoreAside(backup, destination, discard: true);
        }
    }

    /// <summary>把 path 暂挪到同级的 .aurora-backup 里，返回备份目录（不存在则返回 null）。</summary>
    private static string? MoveAside(string path)
    {
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        try
        {
            var backupRoot = Path.Combine(parent, ".aurora-backup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);

            var destination = Path.Combine(backupRoot, Path.GetFileName(path));
            if (isFile)
            {
                File.Move(path, destination, true);
            }
            else
            {
                Directory.Move(path, destination);
            }

            return backupRoot;
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[update] 备份 {path} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>还原（或直接丢弃）MoveAside 暂存起来的文件/目录。</summary>
    private static void RestoreAside(string? backupRoot, string originalPath, bool discard)
    {
        if (backupRoot is null)
        {
            return;
        }

        try
        {
            var moved = Path.Combine(backupRoot, Path.GetFileName(originalPath));

            if (!discard)
            {
                if (File.Exists(moved))
                {
                    File.Move(moved, originalPath, true);
                }
                else if (Directory.Exists(moved))
                {
                    if (Directory.Exists(originalPath))
                    {
                        Directory.Delete(originalPath, true);
                    }

                    Directory.Move(moved, originalPath);
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[update] 还原 {originalPath} 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(backupRoot);

            var parent = Path.GetDirectoryName(backupRoot);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                TryDeleteDirectory(Path.Combine(parent, ".aurora-backup"));
            }
        }
    }

    private static string LoaderLabel(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.UnityModManager => "UnityModManager",
        ModLoaderKind.MelonLoader => "MelonLoader",
        ModLoaderKind.BepInEx => "BepInEx",
        _ => loader.DisplayName(),
    };

    private InstallResult InstallUmmCompat(IReadOnlyList<PackageEntry> entries)
    {
        var gameRoot = Path.GetFullPath(GamePath);
        var staging = Path.Combine(Path.GetTempPath(), $"aurora-ummcompat-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var extracted = ExtractEntries(entries, staging, prefix: null, requireInfoJson: false);
            if (extracted == 0)
            {
                return new InstallResult(false, "UMMBridge 包里没有可安装的文件。");
            }

            UmmBridgePackage.Normalize(staging, gameRoot);

            var isNewBridge = File.Exists(Path.Combine(staging, "Plugins", "UMMBridge.dll")) ||
                              UmmBridgePackage.LooksLikeNewLayout(staging);

            // 只复制 Plugins / UserLibs / 游戏数据目录，不碰 Mods。
            CopyDirectoryContents(staging, gameRoot, overwrite: true);

            if (!isNewBridge)
            {
                UmmCompatConfig.Ensure(gameRoot);
            }

            Directory.CreateDirectory(Path.Combine(gameRoot, "UMMMods"));
            return new InstallResult(
                true,
                isNewBridge
                    ? "UMMBridge 已安装到 Plugins，UMM 文件已放入 Managed/UnityModManager。"
                    : "UMMCompat 已安装到 Plugins 与 UserLibs。");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装 UMMBridge 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static bool IsUmmCompatPackage(IReadOnlyList<PackageEntry> entries)
    {
        var hasPlugin = entries.Any(e =>
            e.Path.Equals("Plugins/UMMCompat.dll", StringComparison.OrdinalIgnoreCase) ||
            e.Path.Equals("Plugins/UMMBridge.dll", StringComparison.OrdinalIgnoreCase) ||
            e.Path.EndsWith("/Plugins/UMMCompat.dll", StringComparison.OrdinalIgnoreCase) ||
            e.Path.EndsWith("/Plugins/UMMBridge.dll", StringComparison.OrdinalIgnoreCase));

        var hasUserLib = entries.Any(e =>
            e.Path.EndsWith("UserLibs/UnityModManager.dll", StringComparison.OrdinalIgnoreCase));

        // 新版 UMMBridge 不再使用 UserLibs，而是把 UMM 放在
        // __gamename___Data/Managed/UnityModManager。
        var hasBridgeLayout = entries.Any(e =>
            e.Path.Contains(UmmBridgePackage.GameNamePlaceholder, StringComparison.OrdinalIgnoreCase) &&
            e.Path.Contains("Managed/UnityModManager/", StringComparison.OrdinalIgnoreCase));

        return hasPlugin && (hasUserLib || hasBridgeLayout);
    }

    private static bool IsBepInExPackage(IReadOnlyList<PackageEntry> entries)
    {
        return entries.Any(e =>
            e.Path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
            // BepInEx 的约定是小写 plugins/；MelonLoader 的插件目录是 Plugins/。
            e.Path.StartsWith("plugins/", StringComparison.Ordinal));
    }

    private static bool HasMelonStructure(IReadOnlyList<PackageEntry> entries)
    {
        return entries.Any(e =>
            e.Path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("UserLibs/", StringComparison.OrdinalIgnoreCase));
    }

    private static DllLoaderInspector.LoaderFlags InspectPackageDlls(IReadOnlyList<PackageEntry> entries)
    {
        var flags = new DllLoaderInspector.LoaderFlags();

        foreach (var entry in entries.Where(e =>
                     e.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                // 不把 DLL 解压到游戏目录；只在内存里读取元数据。
                if (entry.Entry.Length > 512L * 1024 * 1024)
                {
                    continue;
                }

                using var stream = entry.Entry.Open();
                var current = DllLoaderInspector.Inspect(stream);
                flags.MelonLoader |= current.MelonLoader;
                flags.BepInEx |= current.BepInEx;

                if (flags.MelonLoader && flags.BepInEx)
                {
                    break;
                }
            }
            catch
            {
                // 单个 DLL 读不出来不影响其它 DLL
            }
        }

        return flags;
    }

    /// <summary>
    /// 包同时带多个加载器变体时，只保留目标加载器的文件和两边共用的资源。
    /// </summary>
    private static IReadOnlyList<PackageEntry> FilterEntriesForLoader(
        IReadOnlyList<PackageEntry> entries,
        ModLoaderKind loader)
    {
        var result = new List<PackageEntry>();

        foreach (var entry in entries)
        {
            if (loader == ModLoaderKind.MelonLoader &&
                entry.Path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (loader == ModLoaderKind.MelonLoader &&
                entry.Path.StartsWith("plugins/", StringComparison.Ordinal))
            {
                continue;
            }

            if (loader == ModLoaderKind.BepInEx &&
                (entry.Path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) ||
                 // MelonLoader 的插件目录是 Plugins/（大写 P），要丢掉；
                 // BepInEx 的约定是小写 plugins/，那正是它自己的目录，必须留着。
                 entry.Path.StartsWith("Plugins/", StringComparison.Ordinal) ||
                 entry.Path.StartsWith("UserLibs/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (entry.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = entry.Entry.Open();
                    var flags = DllLoaderInspector.Inspect(stream);
                    if (flags.Any)
                    {
                        if (loader == ModLoaderKind.MelonLoader && flags.BepInEx && !flags.MelonLoader)
                        {
                            continue;
                        }

                        if (loader == ModLoaderKind.BepInEx && flags.MelonLoader && !flags.BepInEx)
                        {
                            continue;
                        }
                    }
                }
                catch
                {
                    // 读不出元数据时保留文件，交给路径规则
                }
            }

            result.Add(entry);
        }

        return result;
    }

    private static ModInfoJson? ReadInfo(PackageEntry entry)
    {
        try
        {
            using var stream = entry.Entry.Open();
            return JsonSerializer.Deserialize<ModInfoJson>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static int ExtractEntries(
        IReadOnlyList<PackageEntry> entries,
        string targetRoot,
        string? prefix,
        bool requireInfoJson) =>
        ExtractEntries(entries, targetRoot, item => StripPrefix(item.Path, prefix), requireInfoJson);

    /// <summary>不需要剥前缀时（更新时已经算好目标相对路径）。</summary>
    private static int ExtractEntries(
        IReadOnlyList<PackageEntry> entries,
        string targetRoot,
        bool requireInfoJson) =>
        ExtractEntries(entries, targetRoot, item => item.Path, requireInfoJson);

    private static string? StripPrefix(string path, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return path;
        }

        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : null;
    }

    private static int ExtractEntries(
        IReadOnlyList<PackageEntry> entries,
        string targetRoot,
        Func<PackageEntry, string?> map,
        bool requireInfoJson)
    {
        var root = Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar;
        var extracted = 0;
        var infoExtracted = false;

        foreach (var item in entries)
        {
            var relative = map(item);
            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Zip Slip
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            item.Entry.ExtractToFile(destination, true);
            extracted++;

            if (Path.GetFileName(destination).Equals("Info.json", StringComparison.OrdinalIgnoreCase))
            {
                infoExtracted = true;
            }
        }

        return requireInfoJson && !infoExtracted ? 0 : extracted;
    }

    private static void CopyDirectoryContents(string source, string target, bool overwrite)
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
            File.Copy(file, destination, overwrite);
        }
    }

    private static string NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string GetDirectoryPrefix(string infoPath)
    {
        var index = infoPath.LastIndexOf('/');
        return index < 0 ? string.Empty : infoPath[..(index + 1)];
    }

    private static bool TryValidateId(string? rawId, out string safeId, out string error)
    {
        safeId = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawId))
        {
            error = "Info.json 缺少 Id 字段。";
            return false;
        }

        var id = rawId.Trim();
        if (id is "." or ".." ||
            Path.IsPathRooted(id) ||
            id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains('/') ||
            id.Contains('\\'))
        {
            error = $"Info.json 的 Id「{rawId}」不能用作目录名。";
            return false;
        }

        var target = Path.GetFullPath(Path.Combine(Path.GetTempPath(), id));
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Info.json 的 Id「{rawId}」不能用作目录名。";
            return false;
        }

        safeId = id;
        return true;
    }

    private List<string> FindFoldersWithModId(string root, string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return [];
        }

        var result = new List<string>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            var infoPath = FindInfoJson(directory);
            if (infoPath is null)
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(infoPath);
                var existing = JsonSerializer.Deserialize<ModInfoJson>(stream, JsonOptions);
                if (existing is not null &&
                    string.Equals(existing.Id, modId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(directory);
                }
            }
            catch
            {
                // 读不出来就当它不是同名 mod
            }
        }

        return result;
    }

    private static byte[]? TryReadPreviousSettings(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, "Settings.xml");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return File.ReadAllBytes(path);
            }
            catch
            {
                // 换下一份
            }
        }

        return null;
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

    private sealed record PackageEntry(ZipArchiveEntry Entry, string Path);

    // ==================================================================
    // 下载安装 / 缓存
    // ==================================================================

    /// <param name="target">
    /// 更新时传入「当前已安装的那个 mod」，安装就会沿用它原来的加载器与目录；
    /// 全新安装（从「在线 Mod」页点安装）传 null 即可。
    /// </param>
    public async Task<InstallResult> InstallFromUrlAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        string? cacheName = null,
        InstalledMod? target = null)
    {
        string? temp = null;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), $"aurora-mod-{Guid.NewGuid():N}.zip");

            var downloadProgress = progress is null
                ? null
                : new Progress<DownloadProgress>(p =>
                {
                    if (p.Percent >= 0)
                    {
                        progress.Report(p.Percent);
                    }
                });

            await DownloadService.DownloadAsync(Http, url, temp, downloadProgress, ct);

            var result = InstallFromZip(temp, target);

            if (result.Success && !string.IsNullOrWhiteSpace(cacheName))
            {
                TrySaveToCache(temp, cacheName!);
            }

            return result;
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"下载安装失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (temp is not null && File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    public static string? GetCachedZip(string cacheName)
    {
        try
        {
            var path = Path.Combine(AppPaths.ModCacheDirectory, SanitizeFileName(cacheName) + ".zip");
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveToCache(string sourceZip, string cacheName)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ModCacheDirectory);
            var target = Path.Combine(AppPaths.ModCacheDirectory, SanitizeFileName(cacheName) + ".zip");
            File.Copy(sourceZip, target, true);
            TrimZipCache();
        }
        catch
        {
            // 缓存失败不影响安装
        }
    }

    private static void TrimZipCache(int maxCount = 15, long maxBytes = 600L * 1024 * 1024)
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.ModCacheDirectory)
                .GetFiles("*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            long total = 0;
            for (var i = 0; i < files.Count; i++)
            {
                total += files[i].Length;
                if (i >= maxCount || total > maxBytes)
                {
                    files[i].Delete();
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // ==================================================================
    // 卸载 / 启停
    // ==================================================================

    public InstallResult Uninstall(InstalledMod mod)
    {
        try
        {
            if (File.Exists(mod.FolderPath))
            {
                File.Delete(mod.FolderPath);
            }
            else if (Directory.Exists(mod.FolderPath))
            {
                Directory.Delete(mod.FolderPath, true);
            }

            InvalidateScan();

            return new InstallResult(true, $"已卸载 {mod.DisplayName}。", mod.Id);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"卸载失败：{ex.Message}");
        }
    }

    public InstallResult SetEnabled(InstalledMod mod, bool enabled)
    {
        try
        {
            if (mod.Loader == ModLoaderKind.UnityModManager)
            {
                Params.SetEnabled(mod.Id, enabled);
                InvalidateScan();

                return new InstallResult(
                    true,
                    enabled ? $"已启用 {mod.DisplayName}。" : $"已禁用 {mod.DisplayName}。",
                    mod.Id);
            }

            var changed = SetDllEnabled(mod.FolderPath, enabled);
            if (!changed)
            {
                return new InstallResult(false, "这个 mod 没有可切换的 DLL。");
            }

            InvalidateScan();

            return new InstallResult(
                true,
                enabled
                    ? $"已启用 {mod.DisplayName}（重启游戏后生效）。"
                    : $"已禁用 {mod.DisplayName}（重启游戏后生效）。",
                mod.Id);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"修改启用状态失败：{ex.Message}");
        }
    }

    private static bool SetDllEnabled(string path, bool enabled)
    {
        if (File.Exists(path))
        {
            return RenameDll(path, enabled);
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var changed = false;
        foreach (var file in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(path, "*.dll.disabled", SearchOption.AllDirectories))
                     .ToList())
        {
            if (RenameDll(file, enabled))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static bool RenameDll(string path, bool enabled)
    {
        try
        {
            var isDisabled = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            if (enabled && isDisabled)
            {
                var target = path[..^".disabled".Length];
                File.Move(path, target, true);
                return true;
            }

            if (!enabled && !isDisabled)
            {
                File.Move(path, path + ".disabled", true);
                return true;
            }
        }
        catch
        {
            // 忽略单个文件
        }

        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/0.1.8");
        return client;
    }
}
