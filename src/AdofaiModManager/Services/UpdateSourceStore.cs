using System.IO;
using System.Text.Json;

namespace AdofaiModManager.Services;

/// <summary>保存用户为某个 mod 手动指定的 GitHub / Repository.json 更新源。</summary>
public sealed class UpdateSourceStore
{
    private readonly string _path;

    private Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public UpdateSourceStore(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _path = path;
        }
        else
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AdofaiModManager");
            _path = Path.Combine(dir, "update-sources.json");
        }

        Load();
    }

    public string? GetOverride(string ummId) =>
        _overrides.TryGetValue(ummId, out var value) ? value : null;

    public void SetOverride(string ummId, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _overrides.Remove(ummId);
        }
        else
        {
            _overrides[ummId] = url.Trim();
        }

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
                _overrides = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            AppPaths.BackupBrokenFile(_path, ex);
            _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 忽略
        }
    }
}
