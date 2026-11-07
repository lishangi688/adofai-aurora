using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 本地 mod 管理：扫描 Mods 文件夹、安装本地 zip、卸载、启用/禁用。
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

    public string GamePath { get; }

    public string ModsPath { get; }

    public string LoaderPath { get; }

    public UmmParamsService Params { get; }

    public ModService(string gamePath)
    {
        GamePath = gamePath;
        ModsPath = Path.Combine(gamePath, "Mods");

        var dataFolder = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName) + "_Data";
        LoaderPath = Path.Combine(gamePath, dataFolder, "Managed", "UnityModManager");

        Params = new UmmParamsService(Path.Combine(LoaderPath, "Params.xml"));
    }

    /// <summary>是否已经装了 UMM loader（有 winhttp.dll 与 Params.xml/loader 目录）。</summary>
    public bool IsLoaderInstalled =>
        Directory.Exists(LoaderPath) &&
        File.Exists(Path.Combine(LoaderPath, "UnityModManager.dll"));

    /// <summary>
    /// 安装过程中在 Mods 下用到的临时文件夹前缀（解压暂存 / 旧版本备份）。
    /// 以点开头、名字固定，扫描时会跳过，残留的也会被顺手清理掉。
    /// </summary>
    private const string TempFolderPrefix = ".aurora-";

    public IReadOnlyList<InstalledMod> Scan()
    {
        var result = new List<InstalledMod>();

        if (!Directory.Exists(ModsPath))
        {
            return result;
        }

        CleanupTempFolders();

        // Params.xml 只读一次（原来每个 mod 都重新解析一遍整个文件）
        var enabledStates = Params.ReadAll();

        foreach (var dir in Directory.GetDirectories(ModsPath))
        {
            if (IsTempFolder(dir))
            {
                continue;
            }

            var infoPath = Path.Combine(dir, "Info.json");
            if (!File.Exists(infoPath))
            {
                continue;
            }

            try
            {
                var info = JsonSerializer.Deserialize<ModInfoJson>(File.ReadAllText(infoPath), JsonOptions);
                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    continue;
                }

                result.Add(new InstalledMod
                {
                    Id = info.Id,
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
                    IsEnabled = !enabledStates.TryGetValue(info.Id, out var enabled) || enabled,
                });
            }
            catch
            {
                // 单个 mod 解析失败不影响其它 mod
            }
        }

        result.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        return result;
    }

    /// <summary>
    /// 安装一个 mod 压缩包。
    ///
    /// 流程刻意做成"先准备好新版本，再替换旧版本"：
    ///  ① 整包解压到 Mods 下的临时文件夹（包坏了 / 磁盘满了，旧版本毫发无损）；
    ///  ② 在临时文件夹里把旧版本的内容补进来（新包里有的文件以新包为准），合并成完整的新版本；
    ///  ③ 把旧版本文件夹整个改名挪开（文件被占用时这一步就会失败 → 原样还原，不会删出半个 mod）；
    ///  ④ 临时文件夹**一次改名**成正式文件夹，最后才删掉旧版本。
    ///
    /// ⚠ 正式文件夹只能靠④这一次改名出现：CleanupTempFolders 认为"正式文件夹存在 = 它是完整的"，
    /// 据此删除旧版本备份。如果在正式文件夹里逐个复制文件，中途被强杀时就会留下半个 mod，
    /// 而唯一完整的旧版本备份会在下次扫描时被删掉。
    /// </summary>
    public InstallResult InstallFromZip(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            return new InstallResult(false, "文件不存在。");
        }

        if (SteamGameLocator.IsGameRunning())
        {
            return new InstallResult(false, SteamGameLocator.GameRunningMessage);
        }

        using var criticalWork = CriticalWork.Begin("安装或更新 Mod");

        string? staging = null;

        try
        {
            ModInfoJson? info;

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                // 包里可能不止一个 Info.json（例如附带的示例 / 依赖），取层级最浅的那个
                var infoEntry = archive.Entries
                    .Where(e => NameOf(e).Equals("Info.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => Normalize(e.FullName).Count(c => c == '/'))
                    .FirstOrDefault();

                if (infoEntry is null)
                {
                    return new InstallResult(false, "这不是 UMM 格式的 mod（压缩包里没有 Info.json，可能是 MelonLoader 等其它加载器的版本）。");
                }

                using (var stream = infoEntry.Open())
                {
                    info = JsonSerializer.Deserialize<ModInfoJson>(stream, JsonOptions);
                }

                if (info is null || string.IsNullOrWhiteSpace(info.Id))
                {
                    return new InstallResult(false, "Info.json 无效或缺少 Id 字段。");
                }

                // Id 会被直接当成文件夹名：像 ".." 或 "C:\xxx" 这样的值会让安装（以及安装前的删除）
                // 跑到 Mods 外面去，必须拦下
                if (!IsSafeFolderName(info.Id))
                {
                    return new InstallResult(false, $"Info.json 里的 Id「{info.Id}」不能用作文件夹名，已拒绝安装。");
                }

                var infoPath = Normalize(infoEntry.FullName);
                var prefix = infoPath[..^"Info.json".Length];

                // ① 先完整解压到临时文件夹
                Directory.CreateDirectory(ModsPath);
                staging = Path.Combine(ModsPath, $"{TempFolderPrefix}new-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                ExtractModFiles(archive, prefix, staging);
            }

            var targetDir = Path.Combine(ModsPath, info.Id);

            // 关键：**所有** Id 相同的旧文件夹都要替换掉。
            // 历史上用 UMM（或手动解压）装过的 mod，文件夹名可能不等于 Info.json 里的 Id
            //（例如文件夹叫 out、Creplay v2.15），如果只处理同名文件夹，
            // 更新后就会留下两份"名字一样、版本不同"的副本。
            var oldFolders = FindFoldersWithModId(info.Id);
            if (Directory.Exists(targetDir) &&
                !oldFolders.Any(f => string.Equals(f, targetDir, StringComparison.OrdinalIgnoreCase)))
            {
                oldFolders.Add(targetDir);
            }

            // ② 覆盖安装：在临时文件夹里把旧版本的内容补进来。
            //    mod 常把自己的配置（各种 json / 子文件夹）写在自己的文件夹里，
            //    所以保留旧目录的全部内容，只用新包里带的文件覆盖。
            //    多份重复文件夹时，优先用"名字正好等于 Id"的那份作为基础。
            var baseFolder = oldFolders.FirstOrDefault(f =>
                                 string.Equals(f, targetDir, StringComparison.OrdinalIgnoreCase))
                             ?? oldFolders.FirstOrDefault();

            if (baseFolder is not null && Directory.Exists(baseFolder))
            {
                OverlayDirectory(baseFolder, staging, overwriteExisting: false);
            }

            // 新包和基础文件夹里都没有 Settings.xml 时，沿用其它重复文件夹里的那份，避免更新后 mod 设置被重置
            var stagedSettings = Path.Combine(staging, "Settings.xml");
            if (!File.Exists(stagedSettings) && TryReadPreviousSettings(oldFolders) is { } previousSettings)
            {
                File.WriteAllBytes(stagedSettings, previousSettings);
            }

            // ③ 旧版本整个挪开（失败就还原，保证不会出现"删了一半"）
            var moved = new List<(string Original, string Backup)>();
            foreach (var folder in oldFolders)
            {
                var backup = Path.Combine(ModsPath, $"{TempFolderPrefix}old-{Guid.NewGuid():N}");
                try
                {
                    Directory.Move(folder, backup);
                    moved.Add((folder, backup));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Restore(moved);
                    return new InstallResult(
                        false,
                        $"旧版本的文件被占用，无法替换（{Path.GetFileName(folder)}）。请关闭游戏和打开着该文件夹的程序后重试。");
                }
            }

            // ④ 合并好的新版本一次改名就位（同一个文件夹内改名是原子的）
            try
            {
                Directory.Move(staging, targetDir);
                staging = null;
            }
            catch
            {
                // 改名失败时正式文件夹并没有被创建，直接还原旧版本即可
                Restore(moved);
                throw;
            }

            foreach (var (_, backup) in moved)
            {
                TryDeleteDirectory(backup);
            }

            var restored = oldFolders.Count > 1 ? $"（已合并 {oldFolders.Count - 1} 个重复文件夹）" : string.Empty;
            return new InstallResult(
                true,
                $"已安装 {info.DisplayName ?? info.Id} {info.Version}。{restored}",
                info.Id);
        }
        catch (InvalidDataException)
        {
            return new InstallResult(false, "安装失败：压缩包已损坏或不是 zip 格式。");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装失败：{ex.Message}");
        }
        finally
        {
            if (staging is not null)
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    /// <summary>把 mod 目录（prefix 之下）的文件解压到 targetDir，带 Zip Slip 防护。</summary>
    private static void ExtractModFiles(ZipArchive archive, string prefix, string targetDir)
    {
        var targetRoot = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // 目录条目
            }

            var relative = Normalize(entry.FullName);

            if (prefix.Length > 0)
            {
                if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // mod 目录之外的文件，跳过
                }

                relative = relative[prefix.Length..];
            }

            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var fullDestination = Path.GetFullPath(
                Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar)));

            // 防 Zip Slip
            if (!fullDestination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(fullDestination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(fullDestination, true);
        }
    }

    /// <summary>zip 里的路径分隔符可能是反斜杠（PowerShell 5.1 打的包），统一成 "/"。</summary>
    private static string Normalize(string fullName) => fullName.Replace('\\', '/');

    private static string NameOf(ZipArchiveEntry entry)
    {
        var path = Normalize(entry.FullName);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>能不能直接当作 Mods 下的一个文件夹名（不含路径、不是 . / ..、没有非法字符、不是保留名）。</summary>
    public static bool IsSafeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        // Windows 会悄悄吃掉末尾的空格和点，"Foo." 实际落到 "Foo"，干脆不接受
        if (name != name.Trim() || name.EndsWith('.'))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Windows 的保留设备名（CON / NUL / COM1 …）：带扩展名也一样不能用
        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            return false;
        }

        return true;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsTempFolder(string path) =>
        Path.GetFileName(path).StartsWith(TempFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 清理上次安装中断留下的临时文件夹。
    ///
    /// ⚠ 只有"解压暂存"和"待删除"可以直接删；**旧版本备份不能删** ——
    /// 如果上次安装正好在"移走旧版 → 放入新版"之间被强杀 / 断电，
    /// 那个备份就是这份 mod 唯一的副本，删掉等于把用户的 mod 弄丢了。
    /// 这种情况要把它按 Info.json 里的 Id 恢复回 Mods 下。
    /// </summary>
    private void CleanupTempFolders()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(ModsPath, TempFolderPrefix + "*"))
            {
                var name = Path.GetFileName(dir);

                if (name.StartsWith(TempFolderPrefix + "old-", StringComparison.OrdinalIgnoreCase))
                {
                    RecoverOldBackup(dir);
                    continue;
                }

                TryDeleteDirectory(dir);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把中断安装时留下的旧版本备份恢复回 Mods 下（能恢复就恢复，恢复不了先留着）。</summary>
    private void RecoverOldBackup(string backupDir)
    {
        try
        {
            var infoPath = Path.Combine(backupDir, "Info.json");
            if (!File.Exists(infoPath))
            {
                // 连 Info.json 都没有，不可能是有效备份
                TryDeleteDirectory(backupDir);
                return;
            }

            ModInfoJson? info;
            using (var stream = File.OpenRead(infoPath))
            {
                info = JsonSerializer.Deserialize<ModInfoJson>(stream, JsonOptions);
            }

            var id = info?.Id;
            if (string.IsNullOrWhiteSpace(id) || !IsSafeFolderName(id!))
            {
                TryDeleteDirectory(backupDir);
                return;
            }

            var target = Path.Combine(ModsPath, id!);
            if (Directory.Exists(target))
            {
                // 已经有正常版本了，这份备份没用了。
                // 能这样判断的前提：正式文件夹只会由 InstallFromZip 的"一次改名"产生，存在就一定是完整的
                TryDeleteDirectory(backupDir);
                return;
            }

            Directory.Move(backupDir, target);
            AppPaths.AppendDebugLog($"[install] 已恢复上次中断安装留下的旧版本：{id}");
        }
        catch
        {
            // 恢复不了就留着，别删
        }
    }

    /// <summary>
    /// 把 source 里的文件复制到 target，**保留 target 里多出来的文件**。
    /// 用来做"覆盖安装"：mod 常把自己的配置写在自己的文件夹里，
    /// 如果直接换成"只有新包内容"的目录，配置就一起没了。
    /// overwriteExisting = false 时 target 里已有的文件保持不动（用于把旧版本补进新包）。
    /// </summary>
    private static void OverlayDirectory(string source, string target, bool overwriteExisting = true)
    {
        var sourceRoot = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(target);

        // 空目录也建出来（有些 mod 靠目录存在来判断）
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, dir);
            var destination = Path.GetFullPath(Path.Combine(targetRoot, relative));

            if (destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(destination);
            }
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.GetFullPath(Path.Combine(targetRoot, relative));

            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 防目录穿越
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (!overwriteExisting && File.Exists(destination))
            {
                continue;
            }

            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void Restore(List<(string Original, string Backup)> moved)
    {
        foreach (var (original, backup) in moved)
        {
            try
            {
                Directory.Move(backup, original);
            }
            catch
            {
                AppPaths.AppendDebugLog($"[install] 还原旧版本失败：{backup} → {original}");
            }
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

    /// <summary>
    /// 找出 Mods 下所有 Info.json.Id 等于指定 Id 的文件夹。
    /// 包含"文件夹名与 Id 不一致"的历史遗留（例如 out、Creplay v2.15）。
    /// </summary>
    private List<string> FindFoldersWithModId(string modId)
    {
        var result = new List<string>();

        if (!Directory.Exists(ModsPath))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(ModsPath))
        {
            if (IsTempFolder(directory))
            {
                continue;
            }

            var infoPath = Path.Combine(directory, "Info.json");
            if (!File.Exists(infoPath))
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

    /// <summary>从旧文件夹里取一份 Settings.xml（优先取文件夹名与 Id 一致的那份）。</summary>
    private static byte[]? TryReadPreviousSettings(List<string> folders)
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

    /// <summary>
    /// 从 URL 下载 zip 并安装（progress 为 0-100）。
    /// cacheName 不为空时，安装成功后会把这个 zip 留在本地缓存里（离线可重装）。
    /// </summary>
    public async Task<InstallResult> InstallFromUrlAsync(
        string url,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        string? cacheName = null)
    {
        using var criticalWork = CriticalWork.Begin("下载并安装 Mod");

        string? temp = null;
        try
        {
            temp = Path.Combine(Path.GetTempPath(), $"amm-mod-{Guid.NewGuid():N}.zip");

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1L;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(temp);

                var buffer = new byte[81920];
                long read = 0;
                int count;
                var lastPercent = -1;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    read += count;
                    if (total > 0)
                    {
                        // 只在百分比变化时报告：每 80KB 刷一次界面纯属浪费
                        var percent = (int)(read * 100 / total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(percent);
                        }
                    }
                }
            }

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

    /// <summary>查本地缓存的 mod 包（离线重装用）。</summary>
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

    /// <summary>限制缓存大小：最多 15 个包 / 600MB，超出的按最旧优先删除。</summary>
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

    public InstallResult Uninstall(InstalledMod mod)
    {
        if (SteamGameLocator.IsGameRunning())
        {
            return new InstallResult(false, SteamGameLocator.GameRunningMessage);
        }

        using var criticalWork = CriticalWork.Begin("卸载 Mod");

        try
        {
            if (Directory.Exists(mod.FolderPath))
            {
                // 先整个改名挪开再删：文件被占用时改名就会失败，不会删出"半个 mod"
                var trash = Path.Combine(ModsPath, $"{TempFolderPrefix}del-{Guid.NewGuid():N}");
                Directory.Move(mod.FolderPath, trash);
                TryDeleteDirectory(trash);
            }

            return new InstallResult(true, $"已卸载 {mod.DisplayName}。", mod.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstallResult(false, $"卸载失败：文件被占用（{ex.Message}）。请关闭打开着该文件夹的程序后重试。");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"卸载失败：{ex.Message}");
        }
    }

    public InstallResult SetEnabled(InstalledMod mod, bool enabled)
    {
        // 游戏运行时 UMM 退出前会把自己内存里的启用状态写回 Params.xml，这里改了也会被覆盖
        if (SteamGameLocator.IsGameRunning())
        {
            return new InstallResult(false, SteamGameLocator.GameRunningMessage);
        }

        using var criticalWork = CriticalWork.Begin("切换 Mod 启用状态");

        try
        {
            Params.SetEnabled(mod.Id, enabled);
            return new InstallResult(true, enabled ? $"已启用 {mod.DisplayName}。" : $"已禁用 {mod.DisplayName}。", mod.Id);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"修改启用状态失败：{ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AdofaiModManager/0.1");
        return client;
    }
}
