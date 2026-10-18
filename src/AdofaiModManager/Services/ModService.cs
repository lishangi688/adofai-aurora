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
    // 扫描
    // ==================================================================

    public IReadOnlyList<InstalledMod> Scan()
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

    private void ScanMelon(string root, Action<InstalledMod> add)
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

    private static (string? Id, string? Name, string? Version, string? Author) ReadMelonManifest(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return (null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return (
                GetString(root, "id", "Id", "name", "Name"),
                GetString(root, "name", "Name", "displayName", "DisplayName"),
                GetString(root, "version", "Version"),
                GetString(root, "author", "Author"));
        }
        catch
        {
            return (null, null, null, null);
        }
    }

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

    public InstallResult InstallFromZip(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            return new InstallResult(false, "文件不存在。");
        }

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

    private InstallResult InstallUmm(
        IReadOnlyList<PackageEntry> entries,
        ModInfoJson info,
        PackageEntry infoEntry,
        bool multipleMods)
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

    private InstallResult InstallMelon(IReadOnlyList<PackageEntry> entries)
    {
        var gameRoot = Path.GetFullPath(GamePath);
        var installEntries = FilterEntriesForLoader(entries, ModLoaderKind.MelonLoader);
        var hasStructured = installEntries.Any(e =>
            e.Path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            e.Path.StartsWith("UserLibs/", StringComparison.OrdinalIgnoreCase));

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

    private InstallResult InstallBepInEx(IReadOnlyList<PackageEntry> entries)
    {
        var gameRoot = Path.GetFullPath(GamePath);
        var installEntries = FilterEntriesForLoader(entries, ModLoaderKind.BepInEx);
        var hasGameStructure = installEntries.Any(e => e.Path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase));
        var hasPluginsPrefix = installEntries.Any(e => e.Path.StartsWith("plugins/", StringComparison.Ordinal));

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
                 entry.Path.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
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
        bool requireInfoJson)
    {
        var root = Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar;
        var extracted = 0;
        var infoExtracted = false;

        foreach (var item in entries)
        {
            var relative = item.Path;
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relative = relative[prefix.Length..];
            }

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

    public async Task<InstallResult> InstallFromUrlAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        string? cacheName = null)
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

            var result = InstallFromZip(temp);

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
