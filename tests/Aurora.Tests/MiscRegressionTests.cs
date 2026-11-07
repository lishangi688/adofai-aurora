using System.IO;
using System.Xml.Linq;
using AdofaiModManager.Services;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>F8：Params.xml 损坏时先备份再重建；写入是原子的。</summary>
public sealed class ParamsTests
{
    [Fact]
    public void BrokenParams_BackedUpAndUserTold()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0");
        var service = game.Mods();
        Directory.CreateDirectory(service.LoaderPath);
        var paramsPath = Path.Combine(service.LoaderPath, "Params.xml");
        const string broken = "<Param><ModParams><Mod Id=\"Bar\" Enabled=\"false\"";
        File.WriteAllText(paramsPath, broken);

        var mod = Assert.Single(service.Scan());
        var result = service.SetEnabled(mod, false);

        Assert.True(result.Success, result.Message);
        Assert.Contains("Params.xml.broken", result.Message);
        Assert.Equal(broken, File.ReadAllText(paramsPath + ".broken"));

        var saved = XDocument.Load(paramsPath);
        Assert.Contains(saved.Descendants("Mod"), m => (string?)m.Attribute("Id") == "Foo" && (string?)m.Attribute("Enabled") == "false");
        Assert.Empty(Directory.GetFiles(service.LoaderPath, "*.tmp"));
    }

    [Fact]
    public void ValidParams_OtherEntriesKept_NoWarning()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0");
        var service = game.Mods();
        Directory.CreateDirectory(service.LoaderPath);
        var paramsPath = Path.Combine(service.LoaderPath, "Params.xml");
        File.WriteAllText(paramsPath, "<Param ShortcutKeyId=\"2\"><ModParams><Mod Id=\"Bar\" Enabled=\"false\" /></ModParams></Param>");

        var result = service.SetEnabled(Assert.Single(service.Scan()), false);

        Assert.True(result.Success);
        Assert.DoesNotContain("broken", result.Message);
        Assert.False(File.Exists(paramsPath + ".broken"));

        var root = XDocument.Load(paramsPath).Root!;
        Assert.Equal("2", (string?)root.Attribute("ShortcutKeyId"));
        Assert.Contains(root.Descendants("Mod"), m => (string?)m.Attribute("Id") == "Bar");
    }
}

/// <summary>F2：游戏目录 / Steam 库路径统一分隔符。</summary>
public sealed class GamePathTests
{
    [Theory]
    [InlineData(@"c:/program files (x86)/steam\steamapps\common\A Dance of Fire and Ice", @"c:\program files (x86)\steam\steamapps\common\A Dance of Fire and Ice")]
    [InlineData("D:/SteamLibrary/steamapps/common/A Dance of Fire and Ice", @"D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice")]
    [InlineData(" D:/Games/ADOFAI/ ", @"D:\Games\ADOFAI")]
    [InlineData(@"D:\Games\ADOFAI\\", @"D:\Games\ADOFAI")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("D:", "D:")]
    [InlineData("", "")]
    public void NormalizePath(string input, string expected) =>
        Assert.Equal(expected, SteamGameLocator.NormalizePath(input));

    /// <summary>注册表 SteamPath 的正斜杠写法和硬编码的默认目录，规范化后必须能去重。</summary>
    [Fact]
    public void RegistryStyleAndDefaultPath_NormalizeToSameLibrary() =>
        Assert.Equal(
            SteamGameLocator.NormalizePath(@"C:\Program Files (x86)\Steam"),
            SteamGameLocator.NormalizePath("c:/program files (x86)/steam"),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>F5：外部链接白名单。</summary>
public sealed class ExternalLinksTests
{
    [Theory]
    [InlineData("https://github.com/lishangi688/adofai-aurora")]
    [InlineData("http://example.com/a")]
    [InlineData("mailto:someone@example.com")]
    public void WebLinks_Allowed(string url) => Assert.True(ExternalLinks.IsAllowed(url));

    [Theory]
    [InlineData(@"file://\\host\share\x.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("search-ms:query=x&crumb=location:\\\\host\\share")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData(@"C:\Windows")]
    [InlineData("relative/path")]
    [InlineData("")]
    public void OtherSchemes_Blocked(string url) => Assert.False(ExternalLinks.IsAllowed(url));

    [Fact]
    public void Open_BlockedScheme_Throws() =>
        Assert.Throws<InvalidOperationException>(() => ExternalLinks.Open("search-ms:query=x"));
}
