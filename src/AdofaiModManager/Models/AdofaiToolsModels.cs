using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AdofaiModManager.Models;

public sealed class ModListPage
{
    public List<ModListItem> Items { get; set; } = [];

    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}

public sealed class ModAuthor
{
    public string? Name { get; set; }

    public string? Username { get; set; }

    public string? Nickname { get; set; }

    public string? AvatarUrl { get; set; }
}

public sealed class ModLatestVersion
{
    public string Id { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string? VersionType { get; set; }

    public string? DisplayName { get; set; }

    public DateTime? PublishedAt { get; set; }
}

public sealed class ModListItem : INotifyPropertyChanged
{
    private ImageSource? _iconSource;

    public string Id { get; set; } = string.Empty;

    public string? ResourceType { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public List<ModAuthor> Authors { get; set; } = [];

    public string? IconUrl { get; set; }

    public int DownloadCount { get; set; }

    public int FavoriteCount { get; set; }

    public int FollowerCount { get; set; }

    public int CommentCount { get; set; }

    public bool? Favorited { get; set; }

    public bool? Followed { get; set; }

    public ModLatestVersion? LatestVersion { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // ---- UI 辅助 ----
    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[..1].ToUpperInvariant();

    [JsonIgnore]
    public string AuthorsLabel => string.Join("、", Authors
        .Select(a => a.Name ?? a.Nickname ?? a.Username ?? string.Empty)
        .Where(s => s.Length > 0));

    [JsonIgnore]
    public string VersionLabel => string.IsNullOrWhiteSpace(LatestVersion?.VersionId)
        ? string.Empty
        : LatestVersion!.VersionId!;

    [JsonIgnore]
    public string DownloadsLabel => DownloadCount >= 10000
        ? $"{DownloadCount / 10000.0:0.#} 万"
        : DownloadCount.ToString();

    /// <summary>本地状态（已安装 / 可更新），由页面在加载后填入。</summary>
    [JsonIgnore]
    public string? LocalState { get; set; }

    /// <summary>图标（页面异步加载后填入）。</summary>
    [JsonIgnore]
    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            _iconSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ModDetail
{
    public string Id { get; set; } = string.Empty;

    public string? ResourceType { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public List<ModAuthor> Authors { get; set; } = [];

    public string? IconUrl { get; set; }

    public int DownloadCount { get; set; }

    public int FavoriteCount { get; set; }

    public string? Description { get; set; }

    public string? Readme { get; set; }

    /// <summary>详情页 Markdown 展示用：简介为空时回退回 README。</summary>
    [JsonIgnore]
    public string? BodyMarkdown => !string.IsNullOrWhiteSpace(Description) ? Description : Readme;

    public string? License { get; set; }

    public string? SourceUrl { get; set; }

    public string? HomepageUrl { get; set; }

    public ModLatestVersion? LatestVersion { get; set; }

    public List<ModVersion> Versions { get; set; } = [];

    public string AuthorsLabel => string.Join("、", Authors
        .Select(a => a.Name ?? a.Nickname ?? a.Username ?? string.Empty)
        .Where(s => s.Length > 0));

    /// <summary>优先选择 UnityModManager 加载器的文件，其次最新版本的第一个文件。</summary>
    public ModVersionFile? PreferredFile
    {
        get
        {
            foreach (var version in Versions)
            {
                foreach (var file in version.Files)
                {
                    if (file.Loaders.Any(l => l.Equals("unitymodmanager", StringComparison.OrdinalIgnoreCase)))
                    {
                        return file;
                    }
                }
            }

            return Versions.FirstOrDefault()?.Files.FirstOrDefault();
        }
    }
}

public sealed class ModVersion
{
    public string Id { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string? VersionType { get; set; }

    public List<string> Tags { get; set; } = [];

    public string? Changelog { get; set; }

    public List<ModVersionFile> Files { get; set; } = [];

    public DateTime? PublishedAt { get; set; }
}

public sealed class ModVersionFile
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public List<SupportedGame> SupportedGames { get; set; } = [];

    public List<string> Loaders { get; set; } = [];

    public long Size { get; set; }

    public string? MimeType { get; set; }

    public string? DownloadUrl { get; set; }

    public string LoadersLabel => Loaders is not { Count: > 0 }
        ? string.Empty
        : string.Join(" / ", Loaders.Select(l => l.Equals("unitymodmanager", StringComparison.OrdinalIgnoreCase)
            ? "UMM"
            : l.Equals("melonloader", StringComparison.OrdinalIgnoreCase)
                ? "MelonLoader"
                : l));

    public string SizeLabel => Size <= 0
        ? string.Empty
        : Size >= 1048576
            ? $"{Size / 1048576.0:0.#} MB"
            : $"{Size / 1024.0:0} KB";
}

public sealed class SupportedGame
{
    public string? Name { get; set; }

    public string? Version { get; set; }
}

public sealed class DownloadIntent
{
    public string Url { get; set; } = string.Empty;

    public string? SourceUrl { get; set; }

    public string? FileName { get; set; }
}
