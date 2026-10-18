using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Data;
using AdofaiModManager.Models;
using AdofaiModManager.Services;

var failures = 0;

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
