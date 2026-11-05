namespace AdofaiModManager.Services;

/// <summary>
/// 标记"正在写入游戏目录"的关键操作（安装 / 更新 / 卸载 Mod、部署或卸载加载器）。
///
/// 主窗口和首次向导在关闭前会查这个状态：误关窗口时先确认，
/// 避免写到一半留下半个 Mod 或半个加载器。计数是可嵌套的：
/// 外层（例如"下载并安装 Mod"）决定提示文案，内层复用同一个描述。
/// </summary>
public static class CriticalWork
{
    private static readonly object Gate = new();

    private static int _count;

    private static string? _description;

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return _count > 0;
            }
        }
    }

    /// <summary>当前操作的简短描述（不含"正在"），没有操作时返回“执行关键操作”。</summary>
    public static string Description
    {
        get
        {
            lock (Gate)
            {
                return _description ?? "执行关键操作";
            }
        }
    }

    /// <summary>开始一段关键操作；用 using 包住，结束后自动清除标记。</summary>
    public static IDisposable Begin(string description)
    {
        lock (Gate)
        {
            if (_count == 0)
            {
                _description = description;
            }

            _count++;
        }

        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            lock (Gate)
            {
                if (_count > 0)
                {
                    _count--;
                }

                if (_count == 0)
                {
                    _description = null;
                }
            }
        }
    }
}
