using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 内核（UMM loader）管理：内置内核、自建更新通道、下载、部署、回滚。
/// </summary>
public sealed class KernelService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly string[] SnapshotFiles =
    [
        "UnityModManager.dll",
        "0Harmony.dll",
        "dnlib.dll",
        "UnityModManager.xml",
        "Config.xml",
    ];

    private readonly LoaderService _loader;

    /// <summary>软件内置的稳定内核目录。</summary>
    public string BundledPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Resources", "UmmLoader");

    /// <summary>本地已下载 / 备份的内核存放目录。</summary>
    public string KernelsRoot { get; }

    public KernelService(LoaderService loader, string? kernelsRoot = null)
    {
        _loader = loader;
        KernelsRoot = kernelsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdofaiModManager", "kernels");
    }

    public KernelInfo? GetBundled() => ReadKernel(BundledPath, "bundled");

    /// <summary>内置 + 本地已下载 / 备份的全部内核。</summary>
    public IReadOnlyList<KernelInfo> GetLocalKernels()
    {
        var list = new List<KernelInfo>();

        var bundled = GetBundled();
        if (bundled is not null)
        {
            list.Add(bundled);
        }

        try
        {
            if (Directory.Exists(KernelsRoot))
            {
                foreach (var dir in Directory.GetDirectories(KernelsRoot))
                {
                    var kernel = ReadKernel(dir, "downloaded");
                    if (kernel is not null)
                    {
                        list.Add(kernel);
                    }
                }
            }
        }
        catch
        {
            // 忽略
        }

        // 按归一化版本去重（例如 0.32.5 与 0.32.5.0 视为同一版本），优先保留靠前的（内置优先）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KernelInfo>();
        foreach (var kernel in list)
        {
            if (seen.Add(NormalizeVersion(kernel.Version)))
            {
                result.Add(kernel);
            }
        }

        return result;
    }

    public KernelInfo? FindLocal(string version)
    {
        return GetLocalKernels().FirstOrDefault(k =>
            string.Equals(k.Version, version, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>已部署到游戏里的内核版本（读取 UnityModManager.dll 的程序集版本）。</summary>
    public string? GetDeployedVersion()
    {
        try
        {
            var dll = Path.Combine(_loader.LoaderPath, "UnityModManager.dll");
            if (!File.Exists(dll))
            {
                return null;
            }

            return System.Reflection.AssemblyName.GetAssemblyName(dll).Version?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>部署指定内核。</summary>
    public InstallResult Deploy(KernelInfo kernel)
    {
        SnapshotDeployed();
        var result = _loader.DeployFromDirectory(kernel.Directory);

        if (result.Success)
        {
            return new InstallResult(true, $"内核 {kernel.Version} 已部署。");
        }

        return result;
    }

    /// <summary>回滚到更早的某个本地内核版本。</summary>
    public InstallResult Rollback()
    {
        var current = GetDeployedVersion();

        var candidates = GetLocalKernels()
            .Where(k => current is null || CompareVersions(k.Version, current) < 0)
            .ToList();

        if (candidates.Count == 0)
        {
            return new InstallResult(false, "没有更早的内核版本可以回滚。");
        }

        var chosen = candidates
            .OrderByDescending(k => k.Version, Comparer<string>.Create(CompareVersions))
            .First();

        var result = Deploy(chosen);
        return result.Success ? new InstallResult(true, $"已回滚到内核 {chosen.Version}。") : result;
    }

    /// <summary>部署前把当前已有内核备份到本地，便于回滚。</summary>
    public void SnapshotDeployed()
    {
        try
        {
            var version = GetDeployedVersion();
            if (string.IsNullOrWhiteSpace(version) || !Directory.Exists(_loader.LoaderPath))
            {
                return;
            }

            var targetDir = Path.Combine(KernelsRoot, Sanitize(version));
            if (Directory.Exists(targetDir))
            {
                return; // 已备份过
            }

            Directory.CreateDirectory(targetDir);

            foreach (var name in SnapshotFiles)
            {
                var source = Path.Combine(_loader.LoaderPath, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(targetDir, name), true);
                }
            }

            if (File.Exists(_loader.WinhttpPath))
            {
                File.Copy(_loader.WinhttpPath, Path.Combine(targetDir, "winhttp_x64.dll"), true);
            }

            File.WriteAllText(
                Path.Combine(targetDir, "kernel.json"),
                JsonSerializer.Serialize(new { version, name = "UnityModManager", note = "部署前自动备份" }, WriteOptions));
        }
        catch
        {
            // 备份失败不阻断部署
        }
    }

    // 备注：自建「内核更新通道（manifest）」已移除 —— 内核更新改为
    // ① 从资源站「工具库」获取最新 UMM；② 用户导入本地 zip。

    /// <summary>下载（并校验、解压）一个内核版本到本地。</summary>
    public Task<(KernelInfo? Kernel, string Message)> DownloadAsync(
        KernelRelease release,
        CancellationToken ct = default) =>
        DownloadAsync(release, null, ct);

    public async Task<(KernelInfo? Kernel, string Message)> DownloadAsync(
        KernelRelease release,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.Version))
        {
            return (null, "清单里的版本号为空。");
        }

        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
        {
            return (null, "该版本没有提供下载地址。");
        }

        string? tempZip = null;
        try
        {
            Directory.CreateDirectory(KernelsRoot);
            var targetDir = Path.Combine(KernelsRoot, Sanitize(release.Version));

            tempZip = Path.Combine(Path.GetTempPath(), $"amm-kernel-{Guid.NewGuid():N}.zip");

            if (File.Exists(release.DownloadUrl))
            {
                File.Copy(release.DownloadUrl, tempZip, true);
            }
            else
            {
                await DownloadService.DownloadAsync(Http, release.DownloadUrl, tempZip, progress, ct);
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = ComputeSha256(tempZip);
                if (!actual.Equals(release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return (null, "校验失败：下载的内核与清单里的 sha256 不一致，已放弃（可能被篡改或下载不完整）。");
                }
            }

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            Directory.CreateDirectory(targetDir);
            ZipFile.ExtractToDirectory(tempZip, targetDir, true);
            NormalizeDirectory(targetDir);

            if (!File.Exists(Path.Combine(targetDir, "UnityModManager.dll")))
            {
                Directory.Delete(targetDir, true);
                return (null, "该内核包里没有找到 UnityModManager.dll，可能不是有效的内核包。");
            }

            await File.WriteAllTextAsync(
                Path.Combine(targetDir, "kernel.json"),
                JsonSerializer.Serialize(new
                {
                    version = release.Version,
                    name = "UnityModManager",
                    upstream = release.Upstream,
                    note = release.Notes,
                }, WriteOptions),
                ct);

            return (new KernelInfo
            {
                Version = release.Version,
                Directory = targetDir,
                Source = "downloaded",
                Notes = release.Notes,
            }, $"内核 {release.Version} 已下载。");
        }
        catch (Exception ex)
        {
            return (null, $"下载内核失败：{ex.Message}");
        }
        finally
        {
            try
            {
                if (tempZip is not null && File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 从本地 zip 或 URL 导入内核包（例如从 Nexus Mods 下载的 UnityModManager）。
    /// </summary>
    public async Task<(KernelInfo? Kernel, string Message)> ImportAsync(
        string source,
        string? version = null,
        CancellationToken ct = default,
        IProgress<DownloadProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, "没有指定内核来源。");
        }

        var release = new KernelRelease
        {
            Version = string.IsNullOrWhiteSpace(version) ? "imported" : version.Trim(),
            DownloadUrl = source.Trim(),
            Notes = "本地导入 / 手动指定",
        };

        var (kernel, message) = await DownloadAsync(release, progress, ct);
        if (kernel is null)
        {
            return (null, message);
        }

        // 用 DLL 里的真实版本号重命名，便于与更新通道对齐
        var realVersion = ReadKernelDllVersion(kernel.Directory);
        if (!string.IsNullOrWhiteSpace(realVersion) &&
            !string.Equals(realVersion, kernel.Version, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var target = Path.Combine(KernelsRoot, Sanitize(realVersion));
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                Directory.Move(kernel.Directory, target);
                await File.WriteAllTextAsync(
                    Path.Combine(target, "kernel.json"),
                    JsonSerializer.Serialize(
                        new { version = realVersion, name = "UnityModManager", note = "本地导入" }, WriteOptions),
                    ct);

                kernel = new KernelInfo
                {
                    Version = realVersion,
                    Directory = target,
                    Source = "downloaded",
                    Notes = "本地导入",
                };
            }
            catch
            {
                // 重命名失败就沿用原版本号
            }
        }

        return (kernel, $"内核 {kernel.Version} 已就绪。");
    }

    private static string? ReadKernelDllVersion(string directory)
    {
        try
        {
            var dll = Path.Combine(directory, "UnityModManager.dll");
            return File.Exists(dll)
                ? System.Reflection.AssemblyName.GetAssemblyName(dll).Version?.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static int CompareVersions(string? a, string? b)
    {
        var left = ParseVersion(a);
        var right = ParseVersion(b);

        for (var i = 0; i < 4; i++)
        {
            var compare = left[i].CompareTo(right[i]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    private static string NormalizeVersion(string? value) => string.Join('.', ParseVersion(value));

    private static int[] ParseVersion(string? value)    {
        var result = new int[4];
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length && i < 4; i++)
        {
            var digits = new string(parts[i].SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            int.TryParse(digits, out result[i]);
        }

        return result;
    }

    private static KernelInfo? ReadKernel(string directory, string source)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var jsonPath = Path.Combine(directory, "kernel.json");
            string? version = null;
            string? notes = null;

            if (File.Exists(jsonPath))
            {
                using var stream = File.OpenRead(jsonPath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("version", out var v))
                {
                    version = v.GetString();
                }

                if (doc.RootElement.TryGetProperty("note", out var n))
                {
                    notes = n.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                var dll = Path.Combine(directory, "UnityModManager.dll");
                if (File.Exists(dll))
                {
                    version = System.Reflection.AssemblyName.GetAssemblyName(dll).Version?.ToString();
                }
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            return new KernelInfo { Version = version, Directory = directory, Source = source, Notes = notes };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>若内核包多套了一层目录，则把内容提上来。</summary>
    private static void NormalizeDirectory(string directory)
    {
        if (File.Exists(Path.Combine(directory, "UnityModManager.dll")))
        {
            return;
        }

        var found = Directory.GetFiles(directory, "UnityModManager.dll", SearchOption.AllDirectories);
        if (found.Length != 1)
        {
            return;
        }

        var parent = Path.GetDirectoryName(found[0]);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return;
        }

        foreach (var entry in Directory.GetFileSystemEntries(parent))
        {
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(directory, name);
            if (File.Exists(entry))
            {
                File.Copy(entry, destination, true);
                File.Delete(entry);
            }
            else if (Directory.Exists(entry))
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }

                Directory.Move(entry, destination);
            }
        }

        if (parent != directory && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent, true);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sanitize(string version)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(version.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AdofaiModManager/0.1");
        return client;
    }
}
