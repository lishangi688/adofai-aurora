using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AdofaiModManager.Services;

/// <summary>
/// 打开资源管理器的公共实现。
///
/// 不要用 <c>explorer.exe "路径"</c>：路径作为带引号的命令行参数传过去时，
/// 资源管理器经常忽略它，结果打开的是「文档」而不是目标目录（Windows 11 上已实测复现）。
/// 这里改用 Shell 提供的接口：
/// · 目录 → ShellExecuteEx 直接打开（不经过命令行解析）；
/// · 文件 → SHOpenFolderAndSelectItems 打开所在目录并选中该文件（相当于 /select，但可靠）。
/// </summary>
public static class ShellExplorer
{
    /// <summary>
    /// 打开目录；如果 <paramref name="path"/> 是文件（例如单个 DLL mod），就打开它所在的目录并选中它。
    /// </summary>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径为空。", nameof(path));
        }

        var full = Path.GetFullPath(path);

        if (Directory.Exists(full))
        {
            OpenDirectory(full);
            return;
        }

        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"找不到要打开的目录：{full}");
        }

        OpenDirectoryAndSelect(dir, Path.GetFileName(full));
    }

    /// <summary>打开目录并选中其中的文件。</summary>
    public static void OpenDirectoryAndSelect(string folder, string fileName)
    {
        var fullFolder = Path.GetFullPath(folder);
        var fullPath = Path.GetFullPath(Path.Combine(fullFolder, fileName));

        var pidlFolder = ILCreateFromPathW(fullFolder);
        var pidlFile = ILCreateFromPathW(fullPath);

        try
        {
            // S_OK = 0；失败（例如文件已被删除）时退化成“至少把目录打开”。
            if (pidlFolder == IntPtr.Zero ||
                pidlFile == IntPtr.Zero ||
                SHOpenFolderAndSelectItems(pidlFolder, 1, [pidlFile], 0) != 0)
            {
                OpenDirectory(fullFolder);
            }
        }
        finally
        {
            if (pidlFolder != IntPtr.Zero)
            {
                ILFree(pidlFolder);
            }

            if (pidlFile != IntPtr.Zero)
            {
                ILFree(pidlFile);
            }
        }
    }

    /// <summary>用 Shell 打开一个目录（不走 explorer.exe 命令行）。</summary>
    private static void OpenDirectory(string folder)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);
}
