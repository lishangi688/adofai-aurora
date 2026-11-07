using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AdofaiModManager.Models;
using AdofaiModManager.Services;

var root = Path.Combine(Path.GetTempPath(), "amm-smoke-" + Guid.NewGuid().ToString("N")[..8]);
var game = Path.Combine(root, "game");
Directory.CreateDirectory(Path.Combine(game, "A Dance of Fire and Ice_Data", "Managed"));
File.WriteAllBytes(Path.Combine(game, "A Dance of Fire and Ice.exe"), [0x4D, 0x5A, 0x90, 0x00]);
var kernelsRoot = Path.Combine(root, "kernels");

var failures = 0;

void Check(string name, bool ok)
{
    if (!ok)
    {
        failures++;
    }

    Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {name}");
}

var loader = new LoaderService(game);
var kernel = new KernelService(loader, kernelsRoot);

Console.WriteLine("== 1. 初始检测（应为未安装）==");
var s1 = loader.Detect();
Check("游戏存在", s1.GameExists);
Check("未安装", !s1.IsInstalled);
Check("不是不完整", !s1.IsPartial);

Console.WriteLine("== 2. 内置内核 ==");
var bundled = kernel.GetBundled();
Check("内置内核存在", bundled is not null);
Console.WriteLine($"    内置版本 = {bundled?.Version}");

Console.WriteLine("== 3. 部署内置内核 ==");
var r1 = kernel.Deploy(bundled!);
Console.WriteLine($"    {r1.Success}: {r1.Message}");
Check("UnityModManager.dll 已部署", File.Exists(Path.Combine(loader.LoaderPath, "UnityModManager.dll")));
Check("0Harmony.dll 已部署", File.Exists(Path.Combine(loader.LoaderPath, "0Harmony.dll")));
Check("winhttp.dll 已部署", File.Exists(loader.WinhttpPath));
Check("doorstop_config.ini 已部署", File.Exists(loader.DoorstopConfigPath));

var ini = File.ReadAllText(loader.DoorstopConfigPath);
Console.WriteLine("    doorstop_config.ini 内容:");
foreach (var line in ini.Split('\n'))
{
    Console.WriteLine("      " + line.TrimEnd('\r'));
}

Check("doorstop target 正确",
    ini.Contains(@"target_assembly = A Dance of Fire and Ice_Data\Managed\UnityModManager\UnityModManager.dll"));
Check("部署后可检测为已安装", loader.Detect().IsInstalled);
Console.WriteLine($"    已部署版本 = {kernel.GetDeployedVersion()}");

Console.WriteLine("== 4. 再部署一次（应生成备份快照）==");
kernel.Deploy(bundled!);
var localAfter = kernel.GetLocalKernels();
Console.WriteLine("    本地内核: " + string.Join("、", localAfter.Select(k => $"{k.Version}({k.Source})")));
Check("生成了本地备份", Directory.Exists(kernelsRoot) && Directory.GetDirectories(kernelsRoot).Length > 0);

Console.WriteLine("== 5. 版本比较 ==");
Check("0.33.0 > 0.32.5", KernelService.CompareVersions("0.33.0", "0.32.5") > 0);
Check("0.32.5 == 0.32.5.0", KernelService.CompareVersions("0.32.5", "0.32.5.0") == 0);
Check("0.32.4 < 0.32.5.0", KernelService.CompareVersions("0.32.4", "0.32.5.0") < 0);

Console.WriteLine("== 6. 构造内核包与更新清单 ==");
var zipPath = Path.Combine(root, "kernel-9.9.9.zip");
using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
{
    foreach (var name in new[] { "UnityModManager.dll", "0Harmony.dll", "dnlib.dll", "UnityModManager.xml", "winhttp_x64.dll", "winhttp_x86.dll" })
    {
        var source = Path.Combine(bundled!.Directory, name);
        if (File.Exists(source))
        {
            zip.CreateEntryFromFile(source, name);
        }
    }
}

string Sha(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

Console.WriteLine("== 7. 下载并解压内核包 ==");
var release = new KernelRelease
{
    Version = "9.9.9",
    DownloadUrl = zipPath,
    Sha256 = Sha(zipPath),
    Notes = "冒烟测试用内核",
};

var (downloaded, message) = await kernel.DownloadAsync(release, allowLocalFile: true);
Console.WriteLine($"    {message}");
Check("下载成功", downloaded is not null);
Check("解压出 UnityModManager.dll", downloaded is not null && File.Exists(Path.Combine(downloaded.Directory, "UnityModManager.dll")));

Console.WriteLine("== 9. sha256 不匹配应被拒绝 ==");
var badZip = Path.Combine(root, "kernel-bad.zip");
File.Copy(zipPath, badZip, true);
var (bad, badMessage) = await kernel.DownloadAsync(new KernelRelease { Version = "0.0.0", DownloadUrl = badZip, Sha256 = "DEADBEEF" }, allowLocalFile: true);
Console.WriteLine($"    {badMessage}");
Check("被拒绝", bad is null);

Console.WriteLine("== 10. 卸载加载器 ==");
var uninstall = loader.Uninstall();
Console.WriteLine($"    {uninstall.Success}: {uninstall.Message}");
Check("winhttp.dll 已移除", !File.Exists(loader.WinhttpPath));
Check("doorstop_config.ini 已移除", !File.Exists(loader.DoorstopConfigPath));
Check("UnityModManager 目录已移除", !Directory.Exists(loader.LoaderPath));

Console.WriteLine("== 11. 卸载后检测 ==");
var s2 = loader.Detect();
Check("不再已安装", !s2.IsInstalled);
Check("不再是部分安装", !s2.IsPartial);

try
{
    Directory.Delete(root, true);
}
catch
{
    // 忽略
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"有 {failures} 项失败 ❌");
return failures == 0 ? 0 : 1;
