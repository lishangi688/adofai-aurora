using System.Diagnostics;
using System.Text;

namespace AdofaiModManager.Models
{
    internal sealed class InvalidPathException : Exception
    {
        public InvalidPathException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 用于自动化转换并且打开Explorer
    /// </summary>
    internal static class PathToExplorer
    {
        public static Process? Open(string path)
        {
            return Process.Start(new ProcessStartInfo("explorer.exe", ConvertString(path)) { UseShellExecute = true });
        }
        public static Process? Select(string path)
        {
            return Process.Start(new ProcessStartInfo("explorer.exe", "/select,"+ConvertString(path)) { UseShellExecute = true });
        }

        private static string ConvertString(string path)
        {
            // 局部变量即可：原来的 [ThreadStatic] 静态字段，初始化器只在第一个访问它的线程上执行，换线程会拿到 null
            var sb = new StringBuilder(path.Length + 2);
            sb.Append('"');
            bool disk = false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (IsInvalidValue(c))
                    throw new InvalidPathException($"Invalid character '{c}' in path");
                else if (c == '\r') // windows 你为什么换行会是\r\n 💀💀💀
                    continue;
                else if (c == ':')
                {
                    if (disk)
                        throw new InvalidPathException($"Invalid character '{c}' in path");
                    disk = true;
                }
                else if (c == '/')
                    c = '\\';
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static bool IsInvalidValue(char c)
        {
            // maybe we can skip \r...?
            return c is '"' or '?' or '*' or '<' or '>' or '|' or (not '\r' and < (char)32);
        }
    }
}
