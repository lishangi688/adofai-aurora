using System.Collections.ObjectModel;

namespace AdofaiModManager.Models;

/// <summary>「已安装」页里按加载器分组显示的容器。</summary>
public sealed class InstalledModGroup
{
    public required ModLoaderKind Loader { get; init; }

    public required ObservableCollection<InstalledMod> Mods { get; init; }

    public string Title => Loader.DisplayName();

    public string Subtitle => Loader switch
    {
        ModLoaderKind.UnityModManager => "Info.json · Mods / UMMMods",
        ModLoaderKind.MelonLoader => "Mods / Plugins / UserLibs",
        ModLoaderKind.BepInEx => "BepInEx/plugins",
        _ => "未识别加载器",
    };

    public int Count => Mods.Count;
}
