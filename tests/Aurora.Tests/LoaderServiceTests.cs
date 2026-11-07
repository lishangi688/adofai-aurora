using System.IO;
using AdofaiModManager.Services;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>F4 / F9：部署与卸载加载器时不破坏其它加载器（BepInEx 等）。</summary>
public sealed class LoaderServiceTests
{
    private const string BepInExConfig = "[General]\nenabled=true\ntarget_assembly=BepInEx/core/BepInEx.Preloader.dll\n";

    /// <summary>造一个最小的内核目录（内容是占位文本，只测文件流转）。</summary>
    private static string MakeKernel(TestGame game, string winhttp = "umm-winhttp", bool withWinhttp = true)
    {
        var dir = Path.Combine(game.Root, "kernel-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "UnityModManager.dll"), "umm");
        if (withWinhttp)
        {
            File.WriteAllText(Path.Combine(dir, "winhttp_x64.dll"), winhttp);
        }

        return dir;
    }

    [Fact]
    public void BepInEx_BackedUpOnDeploy_RestoredOnUninstall()
    {
        using var game = new TestGame();
        var loader = new LoaderService(game.Root);
        File.WriteAllText(loader.WinhttpPath, "bepinex-winhttp");
        File.WriteAllText(loader.DoorstopConfigPath, BepInExConfig);

        var deploy = loader.DeployFromDirectory(MakeKernel(game));

        Assert.True(deploy.Success, deploy.Message);
        Assert.Contains("amm-backup", deploy.Message);
        Assert.Equal("bepinex-winhttp", File.ReadAllText(loader.WinhttpPath + ".amm-backup"));
        Assert.Equal(BepInExConfig, File.ReadAllText(loader.DoorstopConfigPath + ".amm-backup"));
        Assert.Equal("umm-winhttp", File.ReadAllText(loader.WinhttpPath));
        Assert.Contains("UnityModManager", File.ReadAllText(loader.DoorstopConfigPath));

        var uninstall = loader.Uninstall();

        Assert.True(uninstall.Success, uninstall.Message);
        Assert.Equal("bepinex-winhttp", File.ReadAllText(loader.WinhttpPath));
        Assert.Equal(BepInExConfig, File.ReadAllText(loader.DoorstopConfigPath));
        Assert.False(File.Exists(loader.WinhttpPath + ".amm-backup"));
        Assert.False(File.Exists(loader.DoorstopConfigPath + ".amm-backup"));
        Assert.False(Directory.Exists(loader.LoaderPath));
    }

    /// <summary>非内置内核：卸载时靠部署时记下的哈希认出 winhttp 是 Aurora 装的。</summary>
    [Fact]
    public void NonBundledKernel_UninstallRemovesEverything()
    {
        using var game = new TestGame();
        var loader = new LoaderService(game.Root);

        var deploy = loader.DeployFromDirectory(MakeKernel(game, winhttp: "site-kernel-winhttp"));
        Assert.True(deploy.Success, deploy.Message);
        Assert.DoesNotContain("amm-backup", deploy.Message);

        loader.Uninstall();

        Assert.False(File.Exists(loader.WinhttpPath));
        Assert.False(File.Exists(loader.DoorstopConfigPath));
        Assert.False(Directory.Exists(loader.LoaderPath));
    }

    [Fact]
    public void Redeploy_DoesNotCreateBackups()
    {
        using var game = new TestGame();
        var loader = new LoaderService(game.Root);

        loader.DeployFromDirectory(MakeKernel(game));
        loader.DeployFromDirectory(MakeKernel(game, winhttp: "newer-winhttp"));

        Assert.Empty(Directory.GetFiles(game.Root, "*.amm-backup*"));
        Assert.Equal("newer-winhttp", File.ReadAllText(loader.WinhttpPath));
    }

    /// <summary>旧版本 Aurora 部署的资源站内核没有哈希记录：doorstop 指向 UMM 时不能把 winhttp 当成别人的。</summary>
    [Fact]
    public void LegacyUmmSetupWithoutMarker_NotTreatedAsForeign()
    {
        using var game = new TestGame();
        var loader = new LoaderService(game.Root);
        File.WriteAllText(loader.WinhttpPath, "old-site-winhttp");
        File.WriteAllText(
            loader.DoorstopConfigPath,
            "[General]\nenabled = true\ntarget_assembly = A Dance of Fire and Ice_Data/Managed/UnityModManager/UnityModManager.dll\n");

        loader.DeployFromDirectory(MakeKernel(game));

        Assert.Empty(Directory.GetFiles(game.Root, "*.amm-backup*"));
    }

    /// <summary>F9：内核包缺注入组件时，游戏目录里什么都不写。</summary>
    [Fact]
    public void IncompleteKernel_RefusedBeforeAnyWrite()
    {
        using var game = new TestGame();
        var loader = new LoaderService(game.Root);

        var result = loader.DeployFromDirectory(MakeKernel(game, withWinhttp: false));

        Assert.False(result.Success);
        Assert.False(Directory.Exists(loader.LoaderPath));
        Assert.False(File.Exists(loader.WinhttpPath));
        Assert.False(File.Exists(loader.DoorstopConfigPath));
    }
}
