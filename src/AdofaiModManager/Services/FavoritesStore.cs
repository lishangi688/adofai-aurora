using System.IO;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>本地收藏（保存在本机，不依赖资源站账号）。</summary>
public sealed class FavoritesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    private List<FavoriteMod> _items = [];

    public FavoritesStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdofaiModManager");
        _path = Path.Combine(dir, "favorites.json");
        Load();
    }

    public IReadOnlyList<FavoriteMod> Items => _items;

    public bool Contains(string siteId) =>
        _items.Any(i => string.Equals(i.SiteId, siteId, StringComparison.OrdinalIgnoreCase));

    public void Add(FavoriteMod item)
    {
        if (Contains(item.SiteId))
        {
            return;
        }

        _items.Insert(0, item);
        Save();
    }

    public void Remove(string siteId)
    {
        _items.RemoveAll(i => string.Equals(i.SiteId, siteId, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _items = JsonSerializer.Deserialize<List<FavoriteMod>>(File.ReadAllText(_path)) ?? [];
            }
        }
        catch (Exception ex)
        {
            AppPaths.BackupBrokenFile(_path, ex);
            _items = [];
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_items, JsonOptions));
        }
        catch
        {
            // 忽略
        }
    }
}
