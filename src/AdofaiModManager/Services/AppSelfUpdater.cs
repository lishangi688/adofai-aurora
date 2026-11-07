using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdofaiModManager.Services;

/// <summary>自动更新准备/执行的结果。</summary>
public sealed record SelfUpdateResult(bool Success, string Message, bool ShouldExit = false);

/// <summary>
/// Aurora 自身更新：下载 → 校验 → 落地 → 交给外部完成替换。
///
/// 为什么要"退出后再替换"：
/// 正在运行的 exe/dll 被系统占用，没法直接覆盖自己。所以：
///   · 绿色版：解压到临时目录，由一个小脚本等 Aurora 退出后复制过去并重启（ShouldExit = true）；
///   · 安装版：下载并打开安装程序，由用户自己走安装向导，安装器负责关闭/重启 Aurora（ShouldExit = false）。
/// </summary>
public static class AppSelfUpdater
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// 下载并准备更新。
    /// 返回结果里的 <see cref="SelfUpdateResult.ShouldExit"/> 为 true 时，调用方应立即退出程序
    /// （绿色版要把"替换文件"交给外部脚本）；安装版则保持运行，由安装器负责关闭与重启。
    /// </summary>
    /// <param name="info">更新信息</param>
    /// <param name="distribution">当前是安装版还是绿色版</param>
    /// <param name="progress">进度文本（可直接显示给用户）</param>
    public static async Task<SelfUpdateResult> ApplyAsync(
        AppUpdateInfo info,
        AppDistribution distribution,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var url = info.UrlFor(distribution);

            if (string.IsNullOrWhiteSpace(url))
            {
                return new SelfUpdateResult(false, "没有找到适合当前版本（安装版 / 绿色版）的下载文件，请手动下载。");
            }

            // 版本号来自远端（tag / 资源站），拼进路径前先清洗：
            // 这个目录随后会被整个删除重建，不能让 "../" 之类的值把它带到别处
            var workRoot = Path.Combine(Path.GetTempPath(), $"aurora-update-{SanitizeFileName(info.Version).Replace("..", "_")}");
            var packageDir = Path.Combine(workRoot, "package");
            var payloadDir = Path.Combine(workRoot, "payload");

            ResetDirectory(workRoot);
            Directory.CreateDirectory(packageDir);
            Directory.CreateDirectory(payloadDir);

            var isInstaller = distribution == AppDistribution.Installed;

            // 文件名必须以「实际选中的下载地址」为准：
            // 安装版会去下 Setup.exe，但 info.FileName 可能带的是绿色版 zip 的名字，
            // 直接用会把 exe 存成 .zip、再当压缩包解压 →
            // "End of Central Directory record could not be found"。
            var fileName = ResolveFileName(info, url, isInstaller);

            var packagePath = Path.Combine(packageDir, SanitizeFileName(fileName));

            // 只有开发用的本地测试包允许走"本地文件"：远端（资源站 / GitHub）给出的地址一律当 URL，
            // 否则一个 \\host\share\x.zip 这样的值会让 File.Exists 去连 SMB 共享
            var verifyNote = string.Empty;

            if (info.IsLocalTestPackage)
            {
                progress?.Report($"正在读取本地更新包 v{info.Version} …");
                File.Copy(url, packagePath, true);
            }
            else
            {
                if (!IsHttpUrl(url))
                {
                    return new SelfUpdateResult(false, "更新地址不是有效的网络地址，已中止。请到发布页手动下载。");
                }

                progress?.Report($"正在下载 v{info.Version} …　0%");
                await DownloadAsync(url, packagePath, progress, info.Version, ct);

                // 用 GitHub Release 里的 SHA256SUMS.txt 核对：资源站下载的包也能核对
                progress?.Report("正在校验下载的文件…");
                switch (await VerifyChecksumAsync(packagePath, info.Version, ct))
                {
                    case ChecksumResult.Mismatch:
                        TryDeleteDirectory(workRoot);
                        return new SelfUpdateResult(
                            false,
                            $"校验失败：下载的文件与 GitHub 发布页公布的 SHA256 不一致，已中止更新（文件可能被篡改或下载不完整）。请到发布页手动下载。");

                    case ChecksumResult.Unavailable:
                        verifyNote = "（未能从 GitHub 获取校验文件，本次未做哈希校验）";
                        break;
                }
            }

            // ---- 安装包：打开安装程序，交给用户走向导 ----
            if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("下载完成，正在打开安装程序…");
                return WithNote(LaunchInstaller(packagePath), verifyNote);
            }

            // ---- 压缩包：解压出绿色版文件 ----
            progress?.Report("正在解压…");
            var extracted = ExtractPackage(packagePath, payloadDir, out var manifestVersion, out var setupInside);

            if (!string.IsNullOrWhiteSpace(manifestVersion) &&
                KernelService.CompareVersions(manifestVersion, info.Version) != 0)
            {
                return new SelfUpdateResult(
                    false,
                    $"压缩包里的版本（{manifestVersion}）与要更新的版本（{info.Version}）不一致，已中止。");
            }

            if (isInstaller)
            {
                // 安装版 + 只有合并包：把包里的安装器取出来，打开给用户装
                if (setupInside is null)
                {
                    return new SelfUpdateResult(false, "压缩包里没有找到安装程序，请手动下载安装。");
                }

                // 安装程序已经解压出来了，下载的压缩包（可能上百 MB）就没用了，顺手删掉
                TryDeleteDirectory(packageDir);

                progress?.Report("正在打开安装程序…");
                return WithNote(LaunchInstaller(setupInside), verifyNote);
            }

            if (!extracted)
            {
                return new SelfUpdateResult(false, "压缩包里没有找到程序文件，已中止。");
            }

            if (AppPaths.DebugLogEnabled)
            {
                var top = Directory.Exists(payloadDir)
                    ? string.Join(", ", Directory.GetFiles(payloadDir).Take(6).Select(Path.GetFileName))
                    : "(无)";
                AppPaths.AppendDebugLog($"[selfupdate] 解压到 {payloadDir}，顶层文件示例：{top}");
            }

            // ---- 绿色版：写更新脚本，等 Aurora 退出后替换并重启 ----
            progress?.Report("准备替换文件…");
            return WithNote(LaunchPortableUpdater(payloadDir, AppContext.BaseDirectory, workRoot), verifyNote);
        }
        catch (OperationCanceledException)
        {
            return new SelfUpdateResult(false, "更新已取消。");
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult(false, $"更新失败：{ex.Message}");
        }
    }

    private static async Task DownloadAsync(
        string url,
        string targetPath,
        IProgress<string>? progress,
        string version,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(targetPath);

        var buffer = new byte[81920];
        long read = 0;
        int count;

        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
            read += count;

            if (total > 0)
            {
                var percent = (int)(read * 100 / total);
                progress?.Report($"正在下载 v{version} …　{percent}%（{read / 1048576.0:0.#} / {total / 1048576.0:0.#} MB）");
            }
            else
            {
                progress?.Report($"正在下载 v{version} …　{read / 1048576.0:0.#} MB");
            }
        }
    }

    /// <summary>
    /// 解压压缩包里的程序文件到 payloadDir。
    /// 支持两种布局：
    ///   · 合并包（资源站）：绿色版文件夹（名字由 update.json 指定）+ Setup.exe + update.json
    ///   · 绿色版包（GitHub）：程序文件直接在根目录
    /// </summary>
    private static bool ExtractPackage(
        string packagePath,
        string payloadDir,
        out string? manifestVersion,
        out string? setupInside)
    {
        manifestVersion = null;
        setupInside = null;

        using var archive = ZipFile.OpenRead(packagePath);

        // 注意：不同打包工具生成的 zip，内部路径分隔符可能是 "/" 也可能是 "\"
        //（PowerShell 5.1 的 Compress-Archive 就是反斜杠），这里统一成 "/" 再判断。
        static string Normalize(string fullName) => fullName.Replace('\\', '/');

        // 先读 update.json：里面有版本号，以及"绿色版文件夹叫什么"
        // （合并包里那个文件夹刻意用产品全名，方便用户直接拖拽）
        var portableFolder = "portable";

        var manifestEntry = archive.Entries.FirstOrDefault(e =>
            Normalize(e.FullName).Equals("update.json", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry is not null)
        {
            try
            {
                using var stream = manifestEntry.Open();
                using var document = JsonDocument.Parse(stream);

                if (document.RootElement.TryGetProperty("version", out var versionElement))
                {
                    manifestVersion = versionElement.GetString();
                }

                if (document.RootElement.TryGetProperty("portable", out var portableElement) &&
                    !string.IsNullOrWhiteSpace(portableElement.GetString()))
                {
                    portableFolder = portableElement.GetString()!.Trim().TrimEnd('/');
                }
            }
            catch
            {
                // 元信息读不出来就按默认布局处理
            }
        }

        // 合并包里的安装器
        var setupEntry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            e.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

        if (setupEntry is not null)
        {
            setupInside = Path.Combine(Path.GetDirectoryName(payloadDir)!, setupEntry.Name);
            setupEntry.ExtractToFile(setupInside, true);
        }

        // 合并包：只取「绿色版文件夹」下的文件；绿色版包：程序文件直接在根目录
        var portablePrefix = portableFolder + "/";

        var hasPortableFolder = archive.Entries.Any(e =>
            Normalize(e.FullName).StartsWith(portablePrefix, StringComparison.OrdinalIgnoreCase));

        var copied = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // 目录条目
            }

            var fullName = Normalize(entry.FullName);
            string relative;

            if (hasPortableFolder)
            {
                if (!fullName.StartsWith(portablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relative = fullName[portablePrefix.Length..];
            }
            else
            {
                // 绿色版包：程序文件直接在根目录
                relative = fullName;
            }

            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(payloadDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            var payloadRoot = Path.GetFullPath(payloadDir) + Path.DirectorySeparatorChar;

            // 防 Zip Slip
            if (!destination.StartsWith(payloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            entry.ExtractToFile(destination, true);
            copied++;
        }

        return copied > 0;
    }

    /// <summary>
    /// 打开安装包，交给用户自己走安装向导（**不做静默安装**）。
    /// 带 /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS：安装器会在需要时自动关闭本程序，
    /// 装完后再自动把它打开，用户不必手动重启。
    /// </summary>
    private static SelfUpdateResult LaunchInstaller(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            });

            return new SelfUpdateResult(
                true,
                "安装程序已打开，请按提示完成更新（安装时会自动关闭 Aurora，装完自动重新打开）。",
                ShouldExit: false);
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult(false, $"打开安装程序失败：{ex.Message}");
        }
    }

    private static SelfUpdateResult LaunchPortableUpdater(
        string payloadDir,
        string targetDir,
        string workRoot)
    {
        try
        {
            var target = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var log = Path.Combine(AppPaths.LogDirectory, "update.log");

            // 当前正在运行的 exe：脚本要等它退出。
            // 更新包里的新 exe：更新完要启动它。两者可能不同名
            // （例如从旧名 AdofaiModManager.exe 换成 Aurora.exe），所以分开判断。
            var currentExe = Path.GetFileName(Environment.ProcessPath ?? "Aurora.exe");
            var newExe = PickNewExeName(payloadDir, currentExe);

            // 按进程号等待，而不是按 exe 名字：
            // 用户同时开着另一份 Aurora（例如别的目录里的绿色版）时，按名字会一直等下去，更新永远不执行
            var pid = Environment.ProcessId;
            var newExePath = Path.Combine(target, newExe);

            Directory.CreateDirectory(AppPaths.LogDirectory);

            var scriptPath = Path.Combine(Path.GetTempPath(), $"aurora-apply-{Guid.NewGuid():N}.cmd");

            // 注意：脚本只能写 ASCII。
            // .NET Core 默认不带 GBK(936) 等旧代码页，而且 cmd 对 UTF-8 中文也不友好，
            // 所以这里的提示/日志一律用英文，避免"编码不可用"这类意外。
            var script = $"""
                @echo off
                set "LOG={log}"
                echo [%date% %time%] waiting for the app to exit... >> "%LOG%"

                :wait
                tasklist /fi "pid eq {pid}" /nh 2>nul | find "{pid}" >nul
                if not errorlevel 1 (
                    ping -n 2 127.0.0.1 >nul
                    goto wait
                )

                echo [%date% %time%] copying new version into "{target}" >> "%LOG%"
                robocopy "{payloadDir}" "{target}" /E /IS /IT /NFL /NDL /NJH /NJS /NP >> "%LOG%" 2>&1
                echo [%date% %time%] robocopy exit code %errorlevel% >> "%LOG%"

                if /i not "{currentExe}"=="{newExe}" del "{target}\{currentExe}" 2>nul

                echo [%date% %time%] starting the app... >> "%LOG%"
                start "" "{newExePath}"

                rmdir /s /q "{payloadDir}" 2>nul
                rmdir /s /q "{workRoot}" 2>nul
                del "%~f0" 2>nul
                """;

            // 关键：cmd.exe 是按"系统 OEM 代码页"读取批处理文件的。
            // 如果脚本里有中文路径（例如 D:\umm优化\...），就必须用同一个代码页写入，
            // 否则 cmd 读到的路径是乱码，robocopy 会复制到错误的位置。
            // 而 .NET Core 默认不带 936 等旧代码页，需要先注册提供程序。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

            // 另外：cmd 对换行很挑，必须是 CRLF（C# 源文件里是 LF）。
            var normalized = script.Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(scriptPath, normalized, encoding);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            return new SelfUpdateResult(true, "正在替换文件，Aurora 即将退出并自动重启完成更新。", ShouldExit: true);
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult(false, $"准备替换失败：{ex.Message}");
        }
    }

    private static void ResetDirectory(string path)
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
            // 删不掉就在原目录上重试
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
            // 删不掉就算了（下次启动系统会清临时目录）
        }
    }

    /// <summary>
    /// 从更新包里挑出"要启动的 exe"。
    /// 优先与当前同名；否则排除 createdump.exe 后、优先名字里带 Aurora 的那个
    /// （自包含发布包里除了程序本身，还有一个 createdump.exe）。
    /// </summary>
    private static string PickNewExeName(string payloadDir, string currentExe)
    {
        try
        {
            var candidates = Directory
                .EnumerateFiles(payloadDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();

            return candidates.FirstOrDefault(n => string.Equals(n, currentExe, StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(n => !n.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase)
                                                     && n.Contains("Aurora", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(n => !n.Equals("createdump.exe", StringComparison.OrdinalIgnoreCase))
                   ?? currentExe;
        }
        catch
        {
            return currentExe;
        }
    }

    /// <summary>
    /// 决定下载文件用什么名字保存。顺序：本地路径 → URL 路径末段（要有扩展名，
    /// 资源站的签名直链通常没有扩展名）→ 更新信息里的文件名 → 兜底。
    ///
    /// 为什么要这么绕：一个 AppUpdateInfo 里可能同时带「安装包」和「绿色版 zip」，
    /// 而 FileName 只存得下其中一个。如果安装版下的是 exe、却用 zip 的名字保存，
    /// 后续会把它当压缩包解压而失败。
    /// </summary>
    private static string ResolveFileName(AppUpdateInfo info, string url, bool isInstaller)
    {
        if (info.IsLocalTestPackage)
        {
            var local = Path.GetFileName(url);
            if (!string.IsNullOrWhiteSpace(local))
            {
                return local;
            }
        }

        try
        {
            var fromUrl = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.'))
            {
                return fromUrl;
            }
        }
        catch
        {
            // 不是合法 URI 就忽略，继续用下面的兜底
        }

        if (!string.IsNullOrWhiteSpace(info.FileName))
        {
            return info.FileName!;
        }

        return isInstaller ? "setup.exe" : "update.zip";
    }

    private enum ChecksumResult
    {
        /// <summary>哈希出现在 SHA256SUMS.txt 里</summary>
        Verified,

        /// <summary>拿到了校验文件，但哈希对不上</summary>
        Mismatch,

        /// <summary>拿不到校验文件（GitHub 连不上 / 旧版本没有发布校验文件）</summary>
        Unavailable,
    }

    /// <summary>
    /// 到 GitHub 本版本的 Release 里取 SHA256SUMS.txt，核对下载文件的哈希。
    ///
    /// 校验文件由 installer\build-installer.ps1 生成，包含绿色版 zip、安装包和资源站合并包三个文件，
    /// 所以不管从哪个通道下载，只要能连上 GitHub 就能核对。
    /// 连不上 GitHub 时（国内常见）退化为不校验，并在结果里注明 —— 资源站通道已经固定了条目 slug，
    /// 校验主要是防"资源站上的文件被替换"。
    /// </summary>
    private static async Task<ChecksumResult> VerifyChecksumAsync(string filePath, string version, CancellationToken ct)
    {
        string sums;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var url = $"https://github.com/{AppUpdateService.GitHubOwner}/{AppUpdateService.GitHubRepo}"
                      + $"/releases/download/v{Uri.EscapeDataString(version)}/SHA256SUMS.txt";

            using var response = await Http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                AppPaths.AppendDebugLog($"[selfupdate] 取校验文件失败：{(int)response.StatusCode}");
                return ChecksumResult.Unavailable;
            }

            sums = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppPaths.AppendDebugLog($"[selfupdate] 取校验文件失败：{ex.Message}");
            return ChecksumResult.Unavailable;
        }

        // 每行：<64 位十六进制>  <文件名>（sha256sum 格式，文件名前可能带 *）
        var known = sums
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)[0])
            .Where(hash => hash.Length == 64 && hash.All(Uri.IsHexDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (known.Count == 0)
        {
            AppPaths.AppendDebugLog("[selfupdate] 校验文件里没有可用的哈希");
            return ChecksumResult.Unavailable;
        }

        string actual;
        using (var stream = File.OpenRead(filePath))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        }

        var ok = known.Contains(actual);
        AppPaths.AppendDebugLog($"[selfupdate] 校验 {Path.GetFileName(filePath)} sha256={actual} 结果={(ok ? "通过" : "不一致")}");
        return ok ? ChecksumResult.Verified : ChecksumResult.Mismatch;
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static SelfUpdateResult WithNote(SelfUpdateResult result, string note) =>
        result.Success && note.Length > 0 ? result with { Message = result.Message + note } : result;

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora/0.1");
        return client;
    }
}
