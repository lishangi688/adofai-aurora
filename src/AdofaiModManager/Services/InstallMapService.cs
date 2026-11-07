using System.IO;
using System.Text.Json;

namespace AdofaiModManager.Services;

/// <summary>
/// 记录「资源站 mod」与「UMM mod Id」的对应关系，安装后写入，
/// 用于判断某个在线 mod 是否已安装 / 是否有更新。
/// </summary>
public sealed class InstallMapService
{
    private readonly string _path;

    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public InstallMapService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdofaiModManager");
        _path = Path.Combine(dir, "install-map.json");
        Load();
    }

    public string? GetUmmId(string siteModId) =>
        _map.TryGetValue(siteModId, out var value) ? value : null;

    public void Set(string siteModId, string ummId)
    {
        if (string.IsNullOrWhiteSpace(siteModId) || string.IsNullOrWhiteSpace(ummId))
        {
            return;
        }

        _map[siteModId] = ummId;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded is not null)
            {
                _map = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            AppPaths.BackupBrokenFile(_path, ex);
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略
        }
    }
}
