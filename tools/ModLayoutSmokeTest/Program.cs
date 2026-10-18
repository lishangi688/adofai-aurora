using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Data;
using AdofaiModManager.Models;
using AdofaiModManager.Services;

var failures = 0;

// 自检只操作临时目录，绕开"游戏正在运行"的拦截（开发机上游戏可能正好开着）。
GameProcessGuard.Override = () => false;

var rangeCollection = new RangeObservableCollection<int>();
var rangeView = new ListCollectionView(rangeCollection);
rangeCollection.AddRange([1, 2, 3]);
Check("批量集合：AddRange 能正常驱动 CollectionView", rangeView.Count == 3, rangeView.Count.ToString());

void Check(string name, bool condition, string? detail = null)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}{(detail is null ? string.Empty : $"  ({detail})")}");
    if (!condition)
    {
        failures++;
    }
}

var actualGame = args.Length > 0
    ? args[0]
    : @"I:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice";

if (Directory.Exists(actualGame) &&
    File.Exists(Path.Combine(actualGame, "A Dance of Fire and Ice.exe")))
{
    // 只读检查真实环境：不写游戏目录。
    var env = GameLoaderEnvironment.Detect(actualGame);
    var mods = new ModService(actualGame).Scan();

    Check("真实环境：检测到加载器注入", env.ActiveInjection != ModLoaderKind.Unknown,
        env.ActiveInjection.ToString());
    Check("真实环境：UMM mod 目录存在",
        env.UmmModsPath.Equals(Path.Combine(actualGame, "Mods"), StringComparison.OrdinalIgnoreCase) ||
        env.UmmModsPath.Equals(Path.Combine(actualGame, "UMMMods"), StringComparison.OrdinalIgnoreCase),
        env.UmmModsPath);
    Check("真实环境：扫描到至少 10 个 mod", mods.Count >= 10, mods.Count.ToString());
    Check("真实环境：包含 Iridium", mods.Any(m => m.Id.Equals("Iridium", StringComparison.OrdinalIgnoreCase)));
    Check("真实环境：没有重复路径",
        mods.Select(m => $"{m.Loader}|{m.FolderPath}").Distinct(StringComparer.OrdinalIgnoreCase).Count() == mods.Count);
}
else
{
    Console.WriteLine($"SKIP  真实环境检查（找不到游戏目录：{actualGame}）");
}

// ---- 在线文件选择 ----
var dualVersion = new ModVersion
{
    VersionId = "1.0.0",
    Files =
    [
        new ModVersionFile { Name = "mod-umm.zip", Loaders = ["unitymodmanager"] },
        new ModVersionFile { Name = "mod-melon.zip", Loaders = ["melonloader"] },
    ],
};

var ummOnlyRoot = CreateUmmOnlySandbox();
Check("文件选择：UMM 环境优先选 UMM 文件",
    ModFileSelector.Pick(dualVersion, GameLoaderEnvironment.Detect(ummOnlyRoot))?.Name == "mod-umm.zip");

var mlOnlyRoot = CreateSandbox();
WriteFile(Path.Combine(mlOnlyRoot, "MelonLoader", "MelonLoader.version"), "0.7.3");
WriteFile(Path.Combine(mlOnlyRoot, "version.dll"), string.Empty);
Check("文件选择：MelonLoader 环境优先选 Melon 文件",
    ModFileSelector.Pick(dualVersion, GameLoaderEnvironment.Detect(mlOnlyRoot))?.Name == "mod-melon.zip");

// ---- 加载器组合规则 ----
Check("组合：UMM 单独可用",
    LoaderSelectionRules.Validate(new LoaderSelection { Umm = true }) is null);
Check("组合：UMM + ML 不带 UMMCompat 被拒绝",
    LoaderSelectionRules.Validate(new LoaderSelection { Umm = true, MelonLoader = true }) is not null);
Check("组合：UMM + ML + UMMCompat 可用",
    LoaderSelectionRules.Validate(new LoaderSelection
    {
        Umm = true,
        MelonLoader = true,
        UmmCompat = true,
    }) is null);
Check("组合：UMM + BepInEx 被拒绝",
    LoaderSelectionRules.Validate(new LoaderSelection { Umm = true, BepInEx = true }) is not null);
Check("组合：ML + BepInEx 被拒绝",
    LoaderSelectionRules.Validate(new LoaderSelection { MelonLoader = true, BepInEx = true }) is not null);

// ---- UMMCompat / UMMBridge 环境：Mods = MelonLoader，UMMMods = UMM ----
var compatRoot = CreateSandbox();
CreateUmmCompatEnvironment(compatRoot);
WriteFile(Path.Combine(compatRoot, "Mods", "MLMod", "MLMod.dll"), string.Empty);
WriteFile(Path.Combine(compatRoot, "UMMMods", "UMMMod", "Info.json"),
    """{"Id":"UMMMod","DisplayName":"UMM Test","Version":"1.0.0","AssemblyName":"UMMMod.dll"}""");
WriteFile(Path.Combine(compatRoot, "UMMMods", "UMMMod", "UMMMod.dll"), string.Empty);

var compatEnv = GameLoaderEnvironment.Detect(compatRoot);
Check("UMMCompat：识别为 MelonLoader 注入", compatEnv.ActiveInjection == ModLoaderKind.MelonLoader);
Check("UMMCompat：识别 UMMCompat 插件", compatEnv.UmmCompatInstalled && compatEnv.UmmCompatEnabled);
Check("UMMCompat：UMM mod 目录重定向到 UMMMods",
    compatEnv.UmmModsPath.Equals(Path.Combine(compatRoot, "UMMMods"), StringComparison.OrdinalIgnoreCase),
    compatEnv.UmmModsPath);

var compatMods = new ModService(compatRoot).Scan();
Check("UMMCompat：同时列出 UMM 与 MelonLoader mod",
    compatMods.Any(m => m.Loader == ModLoaderKind.UnityModManager && m.Id == "UMMMod") &&
    compatMods.Any(m => m.Loader == ModLoaderKind.MelonLoader && m.Id == "MLMod"),
    string.Join(", ", compatMods.Select(m => $"{m.Loader}:{m.Id}")));

var bridgeRoot = CreateSandbox();
CreateUmmCompatEnvironment(bridgeRoot, "UMMBridge.dll");
Check("UMMBridge：也能识别兼容层",
    GameLoaderEnvironment.Detect(bridgeRoot).UmmCompatInstalled);

// ---- 迁移：Mods -> UMMMods，再移回 ----
var moveRoot = CreateSandbox();
WriteFile(Path.Combine(moveRoot, "Mods", "UMMMod", "Info.json"),
    """{"Id":"UMMMod","Version":"1.0.0"}""");
WriteFile(Path.Combine(moveRoot, "Mods", "MLMod", "MLMod.dll"), string.Empty);
var moved = UmmModsMigrator.Move(moveRoot, "Mods", "UMMMods");
Check("迁移：UMM -> UMMMods 只移动带 Info.json 的目录",
    moved == 1 &&
    Directory.Exists(Path.Combine(moveRoot, "UMMMods", "UMMMod")) &&
    !Directory.Exists(Path.Combine(moveRoot, "Mods", "UMMMod")) &&
    Directory.Exists(Path.Combine(moveRoot, "Mods", "MLMod")));

var movedBack = UmmModsMigrator.Move(moveRoot, "UMMMods", "Mods");
Check("迁移：UMMMods -> UMM 移回",
    movedBack == 1 && Directory.Exists(Path.Combine(moveRoot, "Mods", "UMMMod")));

// ---- 安装包路由 ----
var installRoot = CreateUmmOnlySandbox();
var service = new ModService(installRoot);

var ummZip = CreateZip("umm.zip", new Dictionary<string, string>
{
    ["MyUmmMod/Info.json"] = """{"Id":"MyUmmMod","DisplayName":"My UMM Mod","Version":"1.0.0","AssemblyName":"MyUmmMod.dll"}""",
    ["MyUmmMod/MyUmmMod.dll"] = "not-a-real-dll",
});
var ummResult = service.InstallFromZip(ummZip);
Check("安装路由：UMM 包 -> Mods/MyUmmMod",
    ummResult.Success && File.Exists(Path.Combine(installRoot, "Mods", "MyUmmMod", "MyUmmMod.dll")),
    ummResult.Message);

var mlZip = CreateZip("ml.zip", new Dictionary<string, string>
{
    ["Mods/MLMod/MLMod.dll"] = "not-a-real-dll",
});
var mlResult = service.InstallFromZip(mlZip);
Check("安装路由：MelonLoader 包 -> Mods",
    mlResult.Success && File.Exists(Path.Combine(installRoot, "Mods", "MLMod", "MLMod.dll")),
    mlResult.Message);

var bepZip = CreateZip("bep.zip", new Dictionary<string, string>
{
    ["BepInEx/plugins/BepPlugin/BepPlugin.dll"] = "not-a-real-dll",
});
var bepResult = service.InstallFromZip(bepZip);
Check("安装路由：BepInEx 包 -> BepInEx/plugins",
    bepResult.Success && File.Exists(Path.Combine(installRoot, "BepInEx", "plugins", "BepPlugin", "BepPlugin.dll")),
    bepResult.Message);

// 用真实 DLL 验证“扁平包靠 DLL 元数据识别加载器”。
var bepLoaderDll = Path.Combine(actualGame, "BepInEx", "plugins", "Iridium", "Iridium.Loader.BepInEx.dll");
var melonLoaderDll = Path.Combine(actualGame, "Mods", "Iridium", "Iridium.Loader.Melon.dll");
if (!File.Exists(melonLoaderDll))
{
    melonLoaderDll = Path.Combine(actualGame, "Mods", "Iridium.Loader.Melon.dll");
}
if (File.Exists(bepLoaderDll))
{
    var flatBepZip = CreateZipBytes("flat-bep.zip", new Dictionary<string, byte[]>
    {
        ["FlatBep.dll"] = File.ReadAllBytes(bepLoaderDll),
    });
    var flatBepResult = service.InstallFromZip(flatBepZip);
    Check("DLL 识别：扁平 BepInEx 包 -> BepInEx/plugins",
        flatBepResult.Success && File.Exists(Path.Combine(installRoot, "BepInEx", "plugins", "FlatBep.dll")),
        flatBepResult.Message);
}
else
{
    Console.WriteLine("SKIP  扁平 BepInEx DLL 识别（找不到测试 DLL）");
}

if (File.Exists(melonLoaderDll))
{
    var flatMelonZip = CreateZipBytes("flat-melon.zip", new Dictionary<string, byte[]>
    {
        ["FlatMelon.dll"] = File.ReadAllBytes(melonLoaderDll),
    });
    var flatMelonResult = service.InstallFromZip(flatMelonZip);
    Check("DLL 识别：扁平 MelonLoader 包 -> Mods",
        flatMelonResult.Success && File.Exists(Path.Combine(installRoot, "Mods", "FlatMelon.dll")),
        flatMelonResult.Message);
}
else
{
    Console.WriteLine("SKIP  扁平 MelonLoader DLL 识别（找不到测试 DLL）");
}

var compatZip = CreateZip("ummcompat.zip", new Dictionary<string, string>
{
    ["Plugins/UMMCompat.dll"] = "not-a-real-dll",
    ["UserLibs/UnityModManager.dll"] = "not-a-real-dll",
    ["UserLibs/Config.xml"] = "<Config><ModsDirectory>UMMMods</ModsDirectory></Config>",
});
var compatResult = service.InstallFromZip(compatZip);
Check("安装路由：旧 UMMCompat 包 -> Plugins + UserLibs + UMMMods",
    compatResult.Success &&
    File.Exists(Path.Combine(installRoot, "Plugins", "UMMCompat.dll")) &&
    File.Exists(Path.Combine(installRoot, "UserLibs", "UnityModManager.dll")) &&
    Directory.Exists(Path.Combine(installRoot, "UMMMods")),
    compatResult.Message);

var bridgeZip = CreateZip("ummbridge.zip", new Dictionary<string, string>
{
    ["Plugins/UMMBridge.dll"] = "not-a-real-dll",
    ["__gamename___Data/Managed/UnityModManager/UnityModManager.dll"] = "not-a-real-dll",
    ["__gamename___Data/Managed/UnityModManager/Config.xml"] = "<Config><ModsDirectory>Mods</ModsDirectory></Config>",
});
var bridgeResult = service.InstallFromZip(bridgeZip);
var bridgeData = Path.Combine(
    installRoot,
    "A Dance of Fire and Ice_Data",
    "Managed",
    "UnityModManager");
Check("安装路由：新版 UMMBridge 包 -> Plugins + Managed/UnityModManager",
    bridgeResult.Success &&
    File.Exists(Path.Combine(installRoot, "Plugins", "UMMBridge.dll")) &&
    File.Exists(Path.Combine(bridgeData, "UnityModManager.dll")) &&
    !Directory.Exists(Path.Combine(installRoot, "__gamename___Data")),
    bridgeResult.Message);

var bridgeEnvRoot = CreateSandbox();
WriteFile(Path.Combine(bridgeEnvRoot, "Plugins", "UMMBridge.dll"), string.Empty);
WriteFile(Path.Combine(bridgeEnvRoot, "A Dance of Fire and Ice_Data", "Managed", "UnityModManager", "UnityModManager.dll"), string.Empty);
WriteFile(Path.Combine(bridgeEnvRoot, "MelonLoader", "MelonLoader.version"), "0.7.3");
WriteFile(Path.Combine(bridgeEnvRoot, "version.dll"), string.Empty);
var bridgeEnv = GameLoaderEnvironment.Detect(bridgeEnvRoot);
Check("新版 UMMBridge：UMM mod 目录为 UMMMods",
    bridgeEnv.UmmCompatInstalled &&
    bridgeEnv.UmmModsPath.Equals(Path.Combine(bridgeEnvRoot, "UMMMods"), StringComparison.OrdinalIgnoreCase),
    bridgeEnv.UmmModsPath);

// ---- MelonLoader 启用 / 禁用 ----
var mlMod = new InstalledMod
{
    Id = "MLMod",
    Loader = ModLoaderKind.MelonLoader,
    DisplayName = "ML Mod",
    FolderPath = Path.Combine(installRoot, "Mods", "MLMod"),
    FolderName = "MLMod",
    IsEnabled = true,
};
var disable = service.SetEnabled(mlMod, false);
Check("启停：MelonLoader 禁用会把 DLL 改成 .disabled",
    disable.Success && File.Exists(Path.Combine(installRoot, "Mods", "MLMod", "MLMod.dll.disabled")));
var enable = service.SetEnabled(mlMod, true);
Check("启停：MelonLoader 启用会恢复 DLL",
    enable.Success && File.Exists(Path.Combine(installRoot, "Mods", "MLMod", "MLMod.dll")));

// ---- 更新：装在哪就按哪种方式更新到哪 ----

// ① 多加载器混合包（UMM + MelonLoader + BepInEx 三个变体）更新已装的 MelonLoader mod
var updateRoot = CreateSandbox();
CreateUmmCompatEnvironment(updateRoot);
WriteFile(Path.Combine(updateRoot, "Mods", "DualMod", "DualMod.dll"), "melon-v1");
var updateService = new ModService(updateRoot);
var installedMelon = updateService.Scan().FirstOrDefault(m => m.Id == "DualMod");
Check("更新准备：扫到 MelonLoader 的 DualMod",
    installedMelon is { Loader: ModLoaderKind.MelonLoader });

var dualZip = CreateZip("dual.zip", new Dictionary<string, string>
{
    ["DualMod/Info.json"] = """{"Id":"DualMod","DisplayName":"Dual Mod","Version":"2.0.0","AssemblyName":"DualMod.dll"}""",
    ["DualMod/DualMod.dll"] = "umm-variant",
    ["Mods/DualMod/DualMod.dll"] = "melon-v2",
    ["BepInEx/plugins/DualMod/DualMod.dll"] = "bep-variant",
});
var dualResult = updateService.InstallFromZip(dualZip, installedMelon);
Check("更新：多加载器包只更新 MelonLoader 那一份（不装成 UMM / BepInEx）",
    dualResult.Success &&
    File.ReadAllText(Path.Combine(updateRoot, "Mods", "DualMod", "DualMod.dll")) == "melon-v2" &&
    !File.Exists(Path.Combine(updateRoot, "Mods", "DualMod", "Info.json")) &&
    !Directory.Exists(Path.Combine(updateRoot, "UMMMods", "DualMod")) &&
    !Directory.Exists(Path.Combine(updateRoot, "BepInEx")),
    dualResult.Message);

// ② 扁平包更新文件夹式 mod：旧文件被替换，不残留
var stalePath = Path.Combine(updateRoot, "Mods", "DualMod", "OldOnly.dll");
WriteFile(stalePath, "stale");
var flatUpdateZip = CreateZip("flat-update.zip", new Dictionary<string, string>
{
    ["DualMod/DualMod.dll"] = "melon-v3",
});
var flatUpdate = updateService.InstallFromZip(
    flatUpdateZip,
    updateService.Scan().FirstOrDefault(m => m.Id == "DualMod"));
Check("更新：扁平包原地替换文件夹，旧文件不残留",
    flatUpdate.Success &&
    File.ReadAllText(Path.Combine(updateRoot, "Mods", "DualMod", "DualMod.dll")) == "melon-v3" &&
    !File.Exists(stalePath),
    flatUpdate.Message);

// ③ 禁用状态在更新后保留
var disabled = updateService.Scan().FirstOrDefault(m => m.Id == "DualMod");
if (disabled is not null)
{
    updateService.SetEnabled(disabled, false);
    var disabledUpdate = updateService.InstallFromZip(
        CreateZip("disabled-update.zip", new Dictionary<string, string>
        {
            ["DualMod/DualMod.dll"] = "melon-v4",
        }),
        updateService.Scan().FirstOrDefault(m => m.Id == "DualMod"));

    Check("更新：禁用中的 mod 更新后仍是禁用",
        disabledUpdate.Success &&
        File.Exists(Path.Combine(updateRoot, "Mods", "DualMod", "DualMod.dll.disabled")) &&
        !File.Exists(Path.Combine(updateRoot, "Mods", "DualMod", "DualMod.dll")),
        disabledUpdate.Message);
}

// ④ BepInEx 插件更新仍留在 BepInEx\plugins
var bepUpdateRoot = CreateSandbox();
WriteFile(Path.Combine(bepUpdateRoot, "BepInEx", "core", "BepInEx.Preloader.dll"), string.Empty);
WriteFile(Path.Combine(bepUpdateRoot, "BepInEx", "plugins", "DualBep", "DualBep.dll"), "bep-v1");
var bepUpdateService = new ModService(bepUpdateRoot);
var installedBep = bepUpdateService.Scan().FirstOrDefault(m => m.Id == "DualBep");
var bepUpdateZip = CreateZip("bep-update.zip", new Dictionary<string, string>
{
    ["DualBep/Info.json"] = """{"Id":"DualBep","Version":"2.0.0","AssemblyName":"DualBep.dll"}""",
    ["plugins/DualBep/DualBep.dll"] = "bep-v2",
});
var bepUpdate = bepUpdateService.InstallFromZip(bepUpdateZip, installedBep);
Check("更新：BepInEx 插件更新后仍在 BepInEx/plugins",
    bepUpdate.Success &&
    File.ReadAllText(Path.Combine(bepUpdateRoot, "BepInEx", "plugins", "DualBep", "DualBep.dll")) == "bep-v2" &&
    !Directory.Exists(Path.Combine(bepUpdateRoot, "Mods")),
    bepUpdate.Message);

// ⑤ UMM 更新：旧文件夹名与 Info.json 的 Id 不同时也不会装出第二份
var ummUpdateRoot = CreateUmmOnlySandbox();
WriteFile(Path.Combine(ummUpdateRoot, "Mods", "out", "Info.json"),
    """{"Id":"AccurateJudgementBar","DisplayName":"AJB","Version":"1.0.0","AssemblyName":"AJB.dll"}""");
WriteFile(Path.Combine(ummUpdateRoot, "Mods", "out", "AJB.dll"), string.Empty);
var ummUpdateService = new ModService(ummUpdateRoot);
var installedUmm = ummUpdateService.Scan().FirstOrDefault(m => m.Id == "AccurateJudgementBar");
var ummUpdateZip = CreateZip("umm-update.zip", new Dictionary<string, string>
{
    ["AccurateJudgementBar/Info.json"] =
        """{"Id":"AccurateJudgementBar","DisplayName":"AJB","Version":"2.0.0","AssemblyName":"AJB.dll"}""",
    ["AccurateJudgementBar/AJB.dll"] = "umm-v2",
});
var ummUpdate = ummUpdateService.InstallFromZip(ummUpdateZip, installedUmm);
Check("更新：UMM mod 只保留一份（不会 out 与新 Id 各装一份）",
    ummUpdate.Success &&
    ummUpdateService.Scan().Count(m => m.Id == "AccurateJudgementBar") == 1 &&
    !Directory.Exists(Path.Combine(ummUpdateRoot, "Mods", "out")),
    ummUpdate.Message);

// ⑥ UMM 的 mod 不会被一个带 Info.json 的 MelonLoader 包更新走
var ummTargetZip = CreateZip("umm-stay.zip", new Dictionary<string, string>
{
    ["AccurateJudgementBar/Info.json"] =
        """{"Id":"AccurateJudgementBar","DisplayName":"AJB","Version":"3.0.0","AssemblyName":"AJB.dll"}""",
    ["AccurateJudgementBar/AJB.dll"] = "umm-v3",
});
var ummStay = ummUpdateService.InstallFromZip(
    ummTargetZip,
    ummUpdateService.Scan().FirstOrDefault(m => m.Id == "AccurateJudgementBar"));
var ummAfter = ummUpdateService.Scan().FirstOrDefault(m => m.Id == "AccurateJudgementBar");
Check("更新：UMM mod 仍更新在 UMM 目录",
    ummStay.Success &&
    ummAfter is { Loader: ModLoaderKind.UnityModManager, Version: "3.0.0" },
    ummStay.Message);

// ⑦ 装在 Plugins 的 MelonLoader 插件，碰到只有小写 plugins/ 的包也不会跑到 BepInEx 去
var pluginUpdateRoot = CreateSandbox();
WriteFile(Path.Combine(pluginUpdateRoot, "MelonLoader", "MelonLoader.version"), "0.7.3");
WriteFile(Path.Combine(pluginUpdateRoot, "version.dll"), string.Empty);
WriteFile(Path.Combine(pluginUpdateRoot, "Plugins", "DualPlugin", "DualPlugin.dll"), "plugin-v1");
var pluginService = new ModService(pluginUpdateRoot);
var installedPlugin = pluginService.Scan().FirstOrDefault(m => m.Id == "DualPlugin");
var pluginUpdate = pluginService.InstallFromZip(
    CreateZip("plugin-update.zip", new Dictionary<string, string>
    {
        ["Plugins/DualPlugin/DualPlugin.dll"] = "plugin-v2",
    }),
    installedPlugin);
Check("更新：Plugins 下的 MelonLoader 插件仍更新在 Plugins",
    pluginUpdate.Success &&
    File.ReadAllText(Path.Combine(pluginUpdateRoot, "Plugins", "DualPlugin", "DualPlugin.dll")) == "plugin-v2" &&
    !Directory.Exists(Path.Combine(pluginUpdateRoot, "BepInEx")),
    pluginUpdate.Message);

// ⑧ UMM 目录里的 mod 不会被同一个包的 MelonLoader 变体顶掉
var mixedRoot = CreateUmmOnlySandbox();
WriteFile(Path.Combine(mixedRoot, "Mods", "MixedMod", "Info.json"),
    """{"Id":"MixedMod","DisplayName":"Mixed Mod","Version":"1.0.0","AssemblyName":"MixedMod.dll"}""");
WriteFile(Path.Combine(mixedRoot, "Mods", "MixedMod", "MixedMod.dll"), string.Empty);
var mixedService = new ModService(mixedRoot);
var installedMixed = mixedService.Scan().FirstOrDefault(m => m.Id == "MixedMod");
var mixedUpdate = mixedService.InstallFromZip(
    CreateZip("mixed-update.zip", new Dictionary<string, string>
    {
        ["MixedMod/Info.json"] =
            """{"Id":"MixedMod","DisplayName":"Mixed Mod","Version":"2.0.0","AssemblyName":"MixedMod.dll"}""",
        ["MixedMod/MixedMod.dll"] = "umm-v2",
    }),
    installedMixed);
Check("更新：包同时带 Info.json 与 MelonLoader 变体时，UMM mod 仍走 UMM 通道",
    mixedUpdate.Success &&
    mixedService.Scan().Count(m => m.Id == "MixedMod") == 1 &&
    mixedService.Scan().First(m => m.Id == "MixedMod").Loader == ModLoaderKind.UnityModManager,
    mixedUpdate.Message);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "所有自检通过。" : $"自检失败：{failures} 项。");
return failures == 0 ? 0 : 1;

static string CreateSandbox()
{
    var path = Path.Combine(Path.GetTempPath(), "aurora-layout-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "A Dance of Fire and Ice.exe"), string.Empty);
    return path;
}

static string CreateUmmOnlySandbox()
{
    var root = CreateSandbox();
    WriteFile(Path.Combine(root, "A Dance of Fire and Ice_Data", "Managed", "UnityModManager", "UnityModManager.dll"), string.Empty);
    WriteFile(Path.Combine(root, "A Dance of Fire and Ice_Data", "Managed", "UnityModManager", "Config.xml"),
        "<Config><ModsDirectory>Mods</ModsDirectory></Config>");
    return root;
}

static void CreateUmmCompatEnvironment(string root, string pluginName = "UMMCompat.dll")
{
    WriteFile(Path.Combine(root, "Plugins", pluginName), string.Empty);
    WriteFile(Path.Combine(root, "UserLibs", "UnityModManager.dll"), string.Empty);
    WriteFile(Path.Combine(root, "UserLibs", "Config.xml"),
        "<Config><ModsDirectory>UMMMods</ModsDirectory></Config>");
    WriteFile(Path.Combine(root, "MelonLoader", "MelonLoader.version"), "0.7.3");
    WriteFile(Path.Combine(root, "version.dll"), string.Empty);
}

static void WriteFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}

static string CreateZip(string name, IReadOnlyDictionary<string, string> files)
{
    var bytes = files.ToDictionary(
        pair => pair.Key,
        pair => new UTF8Encoding(false).GetBytes(pair.Value));
    return CreateZipBytes(name, bytes);
}

static string CreateZipBytes(string name, IReadOnlyDictionary<string, byte[]> files)
{
    var path = Path.Combine(Path.GetTempPath(), "aurora-" + Guid.NewGuid().ToString("N") + "-" + name);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entryName, content) in files)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(content);
    }

    return path;
}
