using System.IO;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>正常安装 / 覆盖更新 / 失败回滚（F1 的新流程）。</summary>
public sealed class ModInstallTests
{
    [Fact]
    public void Update_NewFilesWin_ModConfigAndSettingsKept()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0", ("Foo.dll", "old"), ("Settings.xml", "<mine/>"), ("cfg/user.json", "{u}"));

        var result = game.Mods().InstallFromZip(game.MakeZip("Foo", "2.0", ("Foo.dll", "new"), ("Extra.dll", "x")));

        Assert.True(result.Success, result.Message);
        Assert.Equal("new", game.Read("Foo", "Foo.dll"));
        Assert.Contains("2.0", game.Read("Foo", "Info.json"));
        Assert.Equal("<mine/>", game.Read("Foo", "Settings.xml"));
        Assert.Equal("{u}", game.Read("Foo", "cfg", "user.json"));
        Assert.Equal("x", game.Read("Foo", "Extra.dll"));
        Assert.Empty(game.TempFolders());
    }

    [Fact]
    public void Install_WritesCompleteMarker()
    {
        using var game = new TestGame();

        Assert.True(game.Mods().InstallFromZip(game.MakeZip("Bar", "1.0")).Success);
        Assert.True(File.Exists(Path.Combine(game.ModsPath, "Bar", ".aurora-complete")));
    }

    [Fact]
    public void Update_MergesDuplicateFolders_PrefersFolderNamedAsId()
    {
        using var game = new TestGame();
        game.AddMod("out", "Baz", "0.9", ("Settings.xml", "<fromDup/>"), ("dup.txt", "d"));
        game.AddMod("Baz", "Baz", "1.0", ("base.txt", "b"));

        var result = game.Mods().InstallFromZip(game.MakeZip("Baz", "2.0"));

        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(Path.Combine(game.ModsPath, "out")));
        Assert.Equal("b", game.Read("Baz", "base.txt"));
        Assert.Equal("<missing>", game.Read("Baz", "dup.txt"));
        Assert.Equal("<fromDup/>", game.Read("Baz", "Settings.xml"));
    }

    [Fact]
    public void Update_OldFileLocked_RefusedAndOldVersionIntact()
    {
        using var game = new TestGame();
        var foo = game.AddMod("Foo", "Foo", "1.0", ("Foo.dll", "old"));
        var zip = game.MakeZip("Foo", "2.0", ("Foo.dll", "new"));

        bool success;
        using (new FileStream(Path.Combine(foo, "Foo.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            success = game.Mods().InstallFromZip(zip).Success;
        }

        Assert.False(success);
        Assert.Equal("old", game.Read("Foo", "Foo.dll"));
        Assert.Contains("1.0", game.Read("Foo", "Info.json"));
        Assert.Empty(game.TempFolders());
    }

    [Theory]
    [InlineData("..")]
    [InlineData("Foo.")]
    [InlineData("CON")]
    public void Install_UnsafeId_RefusedAndGameDirIntact(string id)
    {
        using var game = new TestGame();
        File.WriteAllText(Path.Combine(game.Root, "keep.txt"), "k");

        var result = game.Mods().InstallFromZip(game.MakeZip(id, "1.0"));

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(game.Root, "keep.txt")));
        Assert.Empty(game.TempFolders());
    }

    /// <summary>F7：从「已安装」页更新时，包里的 Id 对不上就拒绝，原 mod 不动。</summary>
    [Fact]
    public void Update_IdMismatch_Refused()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0", ("Foo.dll", "old"));

        var result = game.Mods().InstallFromZip(game.MakeZip("Other", "9.9"), expectedId: "Foo");

        Assert.False(result.Success);
        Assert.Contains("Other", result.Message);
        Assert.False(Directory.Exists(Path.Combine(game.ModsPath, "Other")));
        Assert.Equal("old", game.Read("Foo", "Foo.dll"));
        Assert.Empty(game.TempFolders());
    }

    [Fact]
    public void Update_IdMatchesIgnoringCase_Allowed()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0");

        Assert.True(game.Mods().InstallFromZip(game.MakeZip("Foo", "2.0"), expectedId: "foo").Success);
    }
}
