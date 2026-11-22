namespace AdofaiModManager.Models;

public sealed class ToolListPage
{
    public List<ToolListItem> Items { get; set; } = [];

    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }
}

public sealed class ToolListItem
{
    public string Id { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? IconUrl { get; set; }

    public List<ModAuthor> Authors { get; set; } = [];

    public ToolLatestVersion? LatestVersion { get; set; }

    public string VersionLabel => string.IsNullOrWhiteSpace(LatestVersion?.VersionId)
        ? string.Empty
        : LatestVersion!.VersionId!;
}

public sealed class ToolLatestVersion
{
    public string Id { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string? DisplayName { get; set; }

    public string? Tag { get; set; }
}

public sealed class ToolDetail
{
    public string Id { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public List<ToolVersion> Versions { get; set; } = [];
}

public sealed class ToolVersion
{
    public string Id { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string? Changelog { get; set; }

    /// <summary>旧版 API 的单个文件；新版同时返回 files，保留这个字段兼容。</summary>
    public ToolVersionFile? File { get; set; }

    /// <summary>同一版本可包含多个文件（新版站点）。</summary>
    public List<ToolVersionFile> Files { get; set; } = [];

    /// <summary>主要文件：优先 file，其次 files 的第一项。</summary>
    public ToolVersionFile? PrimaryFile => File ?? Files.FirstOrDefault();
}

public sealed class ToolVersionFile
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public long Size { get; set; }

    public string? DownloadUrl { get; set; }

    /// <summary>服务端计算的 SHA256；旧条目可能为 null。</summary>
    public string? Sha256 { get; set; }

    public int DownloadCount { get; set; }
}
