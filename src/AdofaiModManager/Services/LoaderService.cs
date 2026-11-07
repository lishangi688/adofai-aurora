using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace AdofaiModManager.Services;

/// <summary>加载器当前状态。</summary>
public sealed class LoaderStatus
{
    public bool GameExists { get; init; }

    public bool LoaderFolderExists { get; init; }

    public bool LoaderDllExists { get; init; }

    public string? LoaderVersion { get; init; }

    public bool WinhttpExists { get; init; }

    public bool DoorstopConfigExists { get; init; }

    public bool Is64BitGame { get; init; }

    public string[] LegacyBackups { get; init; } = [];

    /// <summary>DoorstopProxy 方式是否已完整安装。</summary>
    public bool IsInstalled => GameExists && LoaderDllExists && WinhttpExists && DoorstopConfigExists;

    /// <summary>有部分文件但不算完整安装。</summary>
    public bool IsPartial =>
        GameExists && !IsInstalled && (LoaderDllExists || WinhttpExists || DoorstopConfigExists || LoaderFolderExists);
}

/// <summary>
/// 接管 UMM 游戏加载器：检测 / 安装(DoorstopProxy) / 卸载。
/// </summary>
public sealed class LoaderService
{
    private static readonly string[] LoaderFiles =
    [
        "UnityModManager.dll",
        "0Harmony.dll",
        "dnlib.dll",
        "UnityModManager.xml",
    ];

    public string GamePath { get; }

    public string ExecutablePath { get; }

    public string DataFolderName { get; }

    public string ManagedPath { get; }

    public string LoaderPath { get; }

    public string WinhttpPath { get; }

    public string DoorstopConfigPath { get; }

    public LoaderService(string gamePath)
    {
        GamePath = gamePath;
        ExecutablePath = Path.Combine(gamePath, SteamGameLocator.GameExeName);
        DataFolderName = Path.GetFileNameWithoutExtension(SteamGameLocator.GameExeName) + "_Data";
        ManagedPath = Path.Combine(gamePath, DataFolderName, "Managed");
        LoaderPath = Path.Combine(ManagedPath, "UnityModManager");
        WinhttpPath = Path.Combine(gamePath, "winhttp.dll");
        DoorstopConfigPath = Path.Combine(gamePath, "doorstop_config.ini");
    }

    public LoaderStatus Detect()
    {
        var loaderDll = Path.Combine(LoaderPath, "UnityModManager.dll");

        string[] legacy = [];
        try
        {
            if (Directory.Exists(ManagedPath))
            {
                legacy = Directory.GetFiles(ManagedPath, "*.original_", SearchOption.TopDirectoryOnly);
            }
        }
        catch
        {
            // 忽略
        }

        return new LoaderStatus
        {
            GameExists = File.Exists(ExecutablePath),
            LoaderFolderExists = Directory.Exists(LoaderPath),
            LoaderDllExists = File.Exists(loaderDll),
            LoaderVersion = ReadLoaderVersion(loaderDll),
            WinhttpExists = File.Exists(WinhttpPath),
            DoorstopConfigExists = File.Exists(DoorstopConfigPath),
            Is64BitGame = Is64BitExecutable(ExecutablePath),
            LegacyBackups = legacy,
        };
    }

    /// <summary>从指定内核目录部署加载器（DoorstopProxy 方式）。</summary>
    public InstallResult DeployFromDirectory(string kernelDirectory)
    {
        if (!File.Exists(ExecutablePath))
        {
            return new InstallResult(false, $"没找到游戏主程序：{ExecutablePath}");
        }

        if (!Directory.Exists(kernelDirectory))
        {
            return new InstallResult(false, $"内核目录不存在：{kernelDirectory}");
        }

        // 游戏运行时 winhttp.dll / UnityModManager.dll 都被占用，复制到一半失败会留下新旧混杂的加载器
        if (SteamGameLocator.IsGameRunning())
        {
            return new InstallResult(false, SteamGameLocator.GameRunningMessage);
        }

        // 所有前置检查都要在第一次写入之前做完：以前先复制了 UnityModManager.dll 才发现缺注入组件，
        // 游戏里会留下"新的 UMM + 旧的注入器"
        if (!File.Exists(Path.Combine(kernelDirectory, "UnityModManager.dll")))
        {
            return new InstallResult(false, "内核缺少 UnityModManager.dll，不是有效的内核包。");
        }

        var is64 = Is64BitExecutable(ExecutablePath);
        var winhttpSource = Path.Combine(kernelDirectory, is64 ? "winhttp_x64.dll" : "winhttp_x86.dll");
        if (!File.Exists(winhttpSource))
        {
            return new InstallResult(false, $"内核缺少注入组件：{Path.GetFileName(winhttpSource)}");
        }

        using var criticalWork = CriticalWork.Begin("安装或修复 UMM 加载器");

        try
        {
            // 其它加载器（BepInEx 等）同样用 winhttp.dll + doorstop_config.ini：
            // 不是 Aurora 装的就先备份，卸载 UMM 加载器时再恢复。必须在任何写入之前判断。
            var backedUp = new List<string>();
            var configIsOurs = File.Exists(DoorstopConfigPath) && DoorstopConfigLooksLikeOurs();

            // doorstop_config 指向 UnityModManager 时，旁边的 winhttp 也属于 UMM 这一套
            //（旧版本 Aurora 部署资源站内核时没留哈希记录，不能因此把它当成别人的）
            if (File.Exists(WinhttpPath) && !configIsOurs && !WinhttpLooksLikeOurs())
            {
                backedUp.Add(BackupForeignFile(WinhttpPath));
            }

            if (File.Exists(DoorstopConfigPath) && !configIsOurs)
            {
                backedUp.Add(BackupForeignFile(DoorstopConfigPath));
            }

            Directory.CreateDirectory(LoaderPath);

            foreach (var name in LoaderFiles)
            {
                var source = Path.Combine(kernelDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(LoaderPath, name), true);
                }
            }

            // 游戏配置：已存在则不覆盖；否则用内核包里的，再退回软件内置的
            var configTarget = Path.Combine(LoaderPath, "Config.xml");
            if (!File.Exists(configTarget))
            {
                var configSource = Path.Combine(kernelDirectory, "Config.xml");
                if (File.Exists(configSource))
                {
                    File.Copy(configSource, configTarget, true);
                }
                else
                {
                    var bundledConfig = Path.Combine(AppContext.BaseDirectory, "Resources", "UmmLoader", "Config.xml");
                    if (File.Exists(bundledConfig))
                    {
                        File.Copy(bundledConfig, configTarget, true);
                    }
                }
            }

            File.Copy(winhttpSource, WinhttpPath, true);

            // 记下这次部署的 winhttp 哈希：资源站 / 本地导入的内核和内置的不是同一个文件，
            // 卸载时靠它认出"这是 Aurora 装的"
            File.WriteAllText(DeployMarkerPath, Sha256Of(WinhttpPath), new UTF8Encoding(false));

            // Doorstop 配置
            var target = $"{DataFolderName}\\Managed\\UnityModManager\\UnityModManager.dll";
            var ini = $"[General]\r\nenabled = true\r\ntarget_assembly = {target}\r\n";
            File.WriteAllText(DoorstopConfigPath, ini, new UTF8Encoding(false));

            var message = "UMM 加载器部署完成。现在可以启动游戏了。";
            if (backedUp.Count > 0)
            {
                message += $"\n检测到其它加载器（例如 BepInEx）的文件，已备份为：{string.Join("、", backedUp)}。"
                           + "卸载 UMM 加载器时会自动恢复。";
            }

            return new InstallResult(true, message);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"部署失败：{ex.Message}");
        }
    }

    /// <summary>部署时记录 winhttp 哈希的文件（放在 UnityModManager 目录里，卸载时一起删掉）。</summary>
    private string DeployMarkerPath => Path.Combine(LoaderPath, "aurora-winhttp.sha256");

    /// <summary>把不是 Aurora 装的文件备份成 *.amm-backup（已有备份时不覆盖，另起一个带时间的名字）。</summary>
    private static string BackupForeignFile(string path)
    {
        var backup = path + ".amm-backup";
        if (File.Exists(backup))
        {
            backup = $"{path}.amm-backup-{DateTime.Now:yyyyMMddHHmmss}";
        }

        File.Copy(path, backup, false);
        return Path.GetFileName(backup);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>卸载加载器。</summary>
    public InstallResult Uninstall()
    {
        if (SteamGameLocator.IsGameRunning())
        {
            return new InstallResult(false, SteamGameLocator.GameRunningMessage);
        }

        using var criticalWork = CriticalWork.Begin("卸载 UMM 加载器");

        try
        {
            var removed = new List<string>();
            var kept = new List<string>();

            // 只删"确实是 Aurora 写的"文件：
            // 有用户用 BepInEx 等其它加载器，它们也用 winhttp.dll / doorstop_config.ini，
            // 一律删掉会把别人的加载器搞坏。
            // 归属要在删任何东西之前一起判断（winhttp 的判断要参考 doorstop_config，也要读 UnityModManager 目录里的哈希记录）。
            var configIsOurs = File.Exists(DoorstopConfigPath) && DoorstopConfigLooksLikeOurs();
            var winhttpIsOurs = File.Exists(WinhttpPath) && (configIsOurs || WinhttpLooksLikeOurs());

            if (File.Exists(DoorstopConfigPath))
            {
                if (configIsOurs)
                {
                    File.Delete(DoorstopConfigPath);
                    removed.Add("doorstop_config.ini");
                }
                else
                {
                    kept.Add("doorstop_config.ini（不是 Aurora 写的）");
                }
            }

            if (File.Exists(WinhttpPath))
            {
                if (winhttpIsOurs)
                {
                    File.Delete(WinhttpPath);
                    removed.Add("winhttp.dll");
                }
                else
                {
                    kept.Add("winhttp.dll（不是 Aurora 装的）");
                }
            }

            // 部署时备份的其它加载器文件：只在原位置已经空出来时恢复，不覆盖上面保留下来的文件
            foreach (var original in new[] { WinhttpPath, DoorstopConfigPath })
            {
                var backup = original + ".amm-backup";
                if (!File.Exists(backup))
                {
                    continue;
                }

                if (File.Exists(original))
                {
                    kept.Add($"{Path.GetFileName(backup)}（原位置已有文件，未恢复）");
                    continue;
                }

                File.Move(backup, original);
                removed.Add($"（已恢复原 {Path.GetFileName(original)}）");
            }

            if (Directory.Exists(LoaderPath))
            {
                Directory.Delete(LoaderPath, true);
                removed.Add("UnityModManager 目录");
            }

            var message = removed.Count == 0
                ? "没有找到需要卸载的加载器文件。"
                : "已卸载：" + string.Join("、", removed);

            if (kept.Count > 0)
            {
                message += "\n以下文件已保留：" + string.Join("、", kept);
            }

            var status = Detect();
            if (status.LegacyBackups.Length > 0)
            {
                message += $"\n⚠ 检测到 {status.LegacyBackups.Length} 个旧版（.original_）备份文件，"
                    + "这是 UMM 早期「Assembly 方式」留下的，需要谨慎处理，暂未自动还原。";
            }

            return new InstallResult(true, message);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"卸载失败：{ex.Message}");
        }
    }

    /// <summary>doorstop_config.ini 是不是 Aurora 写的（内容里会指向 UnityModManager）。</summary>
    private bool DoorstopConfigLooksLikeOurs()
    {
        try
        {
            return File.ReadAllText(DoorstopConfigPath)
                .Contains("UnityModManager", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不了就当不是我们的（宁可留着）
            return false;
        }
    }

    /// <summary>
    /// winhttp.dll 是不是 Aurora 部署的那份：
    /// 和内置的逐字节比较，或者和部署时记下的哈希一致（资源站 / 本地导入的内核）。
    /// </summary>
    private bool WinhttpLooksLikeOurs()
    {
        try
        {
            if (File.Exists(DeployMarkerPath) &&
                string.Equals(File.ReadAllText(DeployMarkerPath).Trim(), Sha256Of(WinhttpPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "UmmLoader");

            foreach (var name in new[] { "winhttp_x64.dll", "winhttp_x86.dll" })
            {
                var candidate = Path.Combine(bundled, name);
                if (File.Exists(candidate) && FilesEqual(candidate, WinhttpPath))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 判断不了就当不是我们的（宁可留着）
        }

        return false;
    }

    private static bool FilesEqual(string a, string b)
    {
        var left = File.ReadAllBytes(a);
        var right = File.ReadAllBytes(b);
        return left.Length == right.Length && left.AsSpan().SequenceEqual(right);
    }

    public void LaunchGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamGameLocator.AppId}")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            LaunchGameDirectly();
        }
    }

    /// <summary>
    /// 直接运行游戏 exe（不经过 Steam）。
    ///
    /// 优点：启动更快、不必等 Steam 界面，且加载器注入照样生效
    ///（Doorstop 是靠 winhttp.dll 代理在进程启动时注入的，与是不是 Steam 启动无关）。
    /// 注意：不走 Steam 时，成就 / 云存档 / 时长统计可能不同步，建议 Steam 已在运行时使用。
    /// </summary>
    public void LaunchGameDirectly()
    {
        Process.Start(new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = GamePath,
        });
    }

    private static string? ReadLoaderVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return AssemblyName.GetAssemblyName(path).Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取 PE 头判断游戏主程序是不是 64 位。</summary>
    private static bool Is64BitExecutable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            if (fs.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
            {
                return true; // 不是 MZ，默认按 64 位处理
            }

            fs.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length)
            {
                return true;
            }

            fs.Position = peOffset + 4;
            var machine = reader.ReadUInt16();
            return machine is 0x8664 or 0xAA64;
        }
        catch
        {
            return true;
        }
    }
}
