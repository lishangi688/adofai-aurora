namespace AdofaiModManager.Models;

/// <summary>当前 mod 属于哪个加载器环境。</summary>
public enum ModLoaderKind
{
    Unknown,
    UnityModManager,
    MelonLoader,
    BepInEx,
}

public static class ModLoaderKindExtensions
{
    public static string DisplayName(this ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.UnityModManager => "UnityModManager",
        ModLoaderKind.MelonLoader => "MelonLoader",
        ModLoaderKind.BepInEx => "BepInEx",
        _ => "其他",
    };

    public static string ShortName(this ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.UnityModManager => "UMM",
        ModLoaderKind.MelonLoader => "ML",
        ModLoaderKind.BepInEx => "BepInEx",
        _ => "其他",
    };
}
