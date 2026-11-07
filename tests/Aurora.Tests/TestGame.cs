using System.IO;
using System.IO.Compression;
using AdofaiModManager.Services;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>测试程序集初始化：不往开发机真实的 Aurora 日志目录里写诊断日志。</summary>
internal static class TestSetup
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => AppPaths.DiagnosticLogEnabled = false;
}

/// <summary>
/// 临时的"假游戏目录"：%TEMP% 下建一个带 Mods 文件夹和游戏主程序占位文件的目录，测试结束删掉。
/// </summary>
internal sealed class TestGame : IDisposable
{
    public TestGame()
    {
        Root = Path.Combine(Path.GetTempPath(), "aurora-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(ModsPath);

        // LoaderService 要求游戏主程序存在；内容不是 PE 时按 64 位处理
        File.WriteAllText(Path.Combine(Root, SteamGameLocator.GameExeName), "not a real exe");
    }

    public string Root { get; }

    public string ModsPath => Path.Combine(Root, "Mods");

    public ModService Mods() => new(Root);

    /// <summary>在 Mods 下（或任意位置）放一个已安装的 mod 文件夹。</summary>
    public string AddMod(string folder, string id, string version, params (string Name, string Body)[] files)
    {
        var path = Path.IsPathRooted(folder) ? folder : Path.Combine(ModsPath, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Info.json"), $$"""{"Id":"{{id}}","Version":"{{version}}"}""");

        foreach (var (name, body) in files)
        {
            var file = Path.Combine(path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, body);
        }

        return path;
    }

    /// <summary>打一个 UMM 格式的 mod 压缩包（文件都放在以 Id 命名的子目录里，和常见的包一致）。</summary>
    public string MakeZip(string id, string version, params (string Name, string Body)[] files)
    {
        var path = Path.Combine(Root, $"{id}-{version}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, string body)
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{id}/{name}").Open());
            writer.Write(body);
        }

        Add("Info.json", $$"""{"Id":"{{id}}","Version":"{{version}}"}""");
        foreach (var (name, body) in files)
        {
            Add(name, body);
        }

        return path;
    }

    public string Read(params string[] parts)
    {
        var path = Path.Combine([ModsPath, .. parts]);
        return File.Exists(path) ? File.ReadAllText(path) : "<missing>";
    }

    public string[] TempFolders(string pattern = ".aurora-*") => Directory.GetDirectories(ModsPath, pattern);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // 清理失败不影响测试结果
        }
    }
}
