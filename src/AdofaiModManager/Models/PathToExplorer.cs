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
        [ThreadStatic]
        public static readonly StringBuilder sb = new();

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
            sb.Length = 0;
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
