using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdofaiModManager.Services;

/// <summary>
/// 通过 Steam 的 libraryfolders.vdf 定位《冰与火之舞》安装目录。
/// </summary>
public sealed class SteamGameLocator
{
    public const int AppId = 977950;
    public const string GameFolderName = "A Dance of Fire and Ice";
    public const string GameExeName = "A Dance of Fire and Ice.exe";

    public const string GameRunningMessage =
        "游戏正在运行，请先关闭《冰与火之舞》再操作（运行中文件被占用，改动也可能在游戏退出时被覆盖）。";

    /// <summary>游戏进程是否在运行（装/卸 mod、部署加载器之前检查，避免改到一半被文件占用卡住）。</summary>
    public static bool IsGameRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExeName));
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>返回所有可能的游戏目录（去重）。</summary>
    public IReadOnlyList<string> FindCandidates()
    {
        var result = new List<string>();

        foreach (var libraryRoot in FindSteamLibraries())
        {
            var candidate = Path.Combine(libraryRoot, "steamapps", "common", GameFolderName);
            if (Directory.Exists(candidate) && !result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// 返回最合适的游戏目录。
    /// 用打分而不是"取第一个"：机器上可能存在旧的游戏副本
    /// （例如 xxx old 备份），靠主程序 / Mods / 加载器文件来区分。
    /// </summary>
    public string? FindBest()
    {
        var candidates = FindCandidates();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(Score)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>给一个游戏目录打分（越高越像"常玩的那个真安装"）。</summary>
    public int Score(string path)
    {
        var score = 0;

        if (File.Exists(Path.Combine(path, GameExeName)))
        {
            score += 100; // 有游戏主程序最重要
        }

        if (Directory.Exists(Path.Combine(path, "Mods")))
        {
            score += 10;
        }

        if (File.Exists(Path.Combine(path, "winhttp.dll")) ||
            File.Exists(Path.Combine(path, "doorstop_config.ini")))
        {
            score += 5; // 已经装过加载器，多半就是常玩的那个
        }

        return score;
    }

    /// <summary>
    /// 统一路径写法：去掉首尾空白，"/" 换成 "\"，去掉结尾的分隔符（盘符根目录除外）。
    ///
    /// Steam 注册表里的 SteamPath 本身就是 "c:/program files (x86)/steam" 这种正斜杠写法，
    /// 不处理的话拼出来的游戏目录会变成 "c:/program files (x86)/steam\steamapps\..."：
    /// 既和 "C:\Program Files (x86)\Steam" 去不了重，还会被存进设置，
    /// Explorer 不认这种混合写法，会直接打开「文档」文件夹。
    ///
    /// 故意不用 Path.GetFullPath：设置页里边输入边保存，"D:" 这种半截输入会被解析成 D 盘的当前目录。
    /// </summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');

        while (normalized.Length > 3 && normalized.EndsWith('\\'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    /// <summary>枚举所有 Steam 库目录（含 Steam 安装目录本身）。</summary>
    public IReadOnlyList<string> FindSteamLibraries()
    {
        var libraries = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = NormalizePath(path);
            if (Directory.Exists(normalized) && !libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(normalized);
            }
        }

        Add(Environment.GetEnvironmentVariable("SteamPath"));
        Add(GetSteamInstallPathFromRegistry());

        // 常见默认位置
        Add(@"C:\Program Files (x86)\Steam");
        Add(@"C:\Program Files\Steam");
        Add(@"D:\Steam");
        Add(@"D:\SteamLibrary");

        // 解析每个候选 Steam 根目录下的 libraryfolders.vdf
        foreach (var root in libraries.ToList())
        {
            foreach (var vdf in new[]
            {
                Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                Path.Combine(root, "config", "libraryfolders.vdf"),
            })
            {
                if (File.Exists(vdf))
                {
                    foreach (var lib in ParseLibraryFolders(vdf))
                    {
                        Add(lib);
                    }
                }
            }
        }

        return libraries;
    }

    private static string? GetSteamInstallPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string currentUserPath && !string.IsNullOrWhiteSpace(currentUserPath))
            {
                return currentUserPath;
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
            {
                return installPath;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            var raw = match.Groups[1].Value.Replace("\\\\", "\\");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                yield return raw;
            }
        }
    }
}
