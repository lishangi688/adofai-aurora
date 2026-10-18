using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

public enum LoaderVersionKind
{
    UmmLocal,
    UmmSite,
    MelonLoader,
    BepInEx,
    UmmCompat,
}

/// <summary>加载器选择框里的一个可安装版本。</summary>
public sealed class LoaderVersionOption
{
    public required string Version { get; init; }

    public required LoaderVersionKind Kind { get; init; }

    public string? DownloadUrl { get; init; }

    public string? FileName { get; init; }

    public string? FileId { get; init; }

    public KernelInfo? Kernel { get; init; }

    public string SourceLabel => Kind switch
    {
        LoaderVersionKind.UmmLocal => Kernel?.Source == "bundled" ? "Aurora 内置" : "本地缓存",
        LoaderVersionKind.UmmSite => "ADOFAITools",
        LoaderVersionKind.MelonLoader => "GitHub",
        LoaderVersionKind.BepInEx => "GitHub",
        LoaderVersionKind.UmmCompat => "GitHub",
        _ => string.Empty,
    };

    public string DisplayLabel => $"{Version}　({SourceLabel})";
}

public sealed class LoaderSelection
{
    public bool Umm { get; init; }

    public bool MelonLoader { get; init; }

    public bool BepInEx { get; init; }

    public bool UmmCompat { get; init; }

    public LoaderVersionOption? UmmVersion { get; init; }

    public LoaderVersionOption? MelonLoaderVersion { get; init; }

    public LoaderVersionOption? BepInExVersion { get; init; }

    public LoaderVersionOption? UmmCompatVersion { get; init; }
}
