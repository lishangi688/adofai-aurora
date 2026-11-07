using System.IO;
using AdofaiModManager.Services;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>中断安装后的恢复（CleanupTempFolders / RecoverOldBackup）。</summary>
public sealed class InterruptedInstallTests
{
    private static string OldBackupName() => ".aurora-old-" + Guid.NewGuid().ToString("N");

    private static string NewStagingName() => ".aurora-new-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// 复审发现的回归：0.1.10～0.1.12.2 在正式目录里逐个复制文件，中断后磁盘上是
    /// 「半个正式目录（没有 Info.json）+ 唯一完整的备份」。升级后第一次扫描不能把备份删掉。
    /// </summary>
    [Fact]
    public void LegacyHalfTarget_BackupIsNotLost()
    {
        using var game = new TestGame();
        var target = Path.Combine(game.ModsPath, "LegacyMod");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "LegacyMod.dll"), "half");
        game.AddMod(OldBackupName(), "LegacyMod", "1.0", ("LegacyMod.dll", "old-complete"), ("Settings.xml", "<mine/>"));
        ModService.TakeRecoveryNotes();

        var mods = game.Mods().Scan();

        // 完整的旧版本回到正式位置
        var mod = Assert.Single(mods);
        Assert.Equal("LegacyMod", mod.Id);
        Assert.Equal("old-complete", game.Read("LegacyMod", "LegacyMod.dll"));
        Assert.Equal("<mine/>", game.Read("LegacyMod", "Settings.xml"));

        // 没法确认完整的目录挪到一边保留，不删
        var aside = Assert.Single(game.TempFolders(".aurora-recovered-*"));
        Assert.Equal("half", File.ReadAllText(Path.Combine(aside, "LegacyMod.dll")));

        // 要告诉用户
        Assert.Single(ModService.TakeRecoveryNotes());

        // 再扫一次也不会动保留下来的目录
        Assert.Single(game.Mods().Scan());
        Assert.Single(game.TempFolders(".aurora-recovered-*"));
    }

    /// <summary>旧状态下即使正式目录有 Info.json（看起来像完整），没有完成标记也不能删备份。</summary>
    [Fact]
    public void LegacyTargetWithInfoJson_NoMarker_BackupKept()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "2.0", ("Foo.dll", "maybe-half"));
        game.AddMod(OldBackupName(), "Foo", "1.0", ("Foo.dll", "old"));

        game.Mods().Scan();

        Assert.Equal("old", game.Read("Foo", "Foo.dll"));
        var aside = Assert.Single(game.TempFolders(".aurora-recovered-*"));
        Assert.Equal("maybe-half", File.ReadAllText(Path.Combine(aside, "Foo.dll")));
        Assert.Empty(game.TempFolders(".aurora-old-*"));
        ModService.TakeRecoveryNotes();
    }

    /// <summary>新流程：改名就位后、删备份前被杀 → 目标带完成标记，备份可以安全删除。</summary>
    [Fact]
    public void MarkedTarget_StaleBackupRemoved()
    {
        using var game = new TestGame();
        Assert.True(game.Mods().InstallFromZip(game.MakeZip("Foo", "2.0")).Success);
        game.AddMod(OldBackupName(), "Foo", "1.0");

        game.Mods().Scan();

        Assert.Empty(game.TempFolders());
        Assert.Contains("2.0", game.Read("Foo", "Info.json"));
    }

    /// <summary>新流程：旧版本已挪走、新版本还没改名就位时被杀 → 从备份恢复。</summary>
    [Fact]
    public void CrashBeforeRename_OldVersionRestored()
    {
        using var game = new TestGame();
        game.AddMod(OldBackupName(), "Foo", "1.0", ("Foo.dll", "old"), ("Settings.xml", "<mine/>"));
        game.AddMod(NewStagingName(), "Foo", "2.0", ("Foo.dll", "new"));

        var mods = game.Mods().Scan();

        Assert.Single(mods);
        Assert.Equal("old", game.Read("Foo", "Foo.dll"));
        Assert.Equal("<mine/>", game.Read("Foo", "Settings.xml"));
        Assert.True(File.Exists(Path.Combine(game.ModsPath, "Foo", ".aurora-complete")));
        Assert.Empty(game.TempFolders());
    }

    /// <summary>新流程：合并阶段被杀（旧版本还在原位）→ 只清掉暂存目录。</summary>
    [Fact]
    public void CrashDuringMerge_OldVersionIntact_StagingCleaned()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0", ("Foo.dll", "old"), ("Settings.xml", "<mine/>"));
        game.AddMod(NewStagingName(), "Foo", "2.0", ("Foo.dll", "half"));

        var mods = game.Mods().Scan();

        Assert.Single(mods);
        Assert.Equal("old", game.Read("Foo", "Foo.dll"));
        Assert.Equal("<mine/>", game.Read("Foo", "Settings.xml"));
        Assert.Empty(game.TempFolders());
    }

    [Fact]
    public void UnknownTempPrefix_IsKept()
    {
        using var game = new TestGame();
        Directory.CreateDirectory(Path.Combine(game.ModsPath, ".aurora-recovered-keepme"));

        game.Mods().Scan();

        Assert.Single(game.TempFolders(".aurora-recovered-*"));
    }
}
