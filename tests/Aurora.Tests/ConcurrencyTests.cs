using System.IO;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AdofaiModManager.Tests;

/// <summary>F13：同一时间只允许一个修改 Mods 目录的操作。</summary>
public sealed class ConcurrencyTests
{
    /// <summary>本地起一个"很慢"的下载服务器：收到请求后等一会儿才返回 zip，用来让第一个安装一直占着锁。</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowServer(byte[] body)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/mod.zip";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                var context = await _listener.GetContextAsync();
                await _release.Task;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            });
        }

        public string Url { get; }

        /// <summary>让服务器把内容发出去，第一个安装就能继续。</summary>
        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
            Release();
            _listener.Close();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    [Fact]
    public async Task SecondOperation_RefusedWhileInstallRunning_AllowedAfterwards()
    {
        using var game = new TestGame();
        game.AddMod("Foo", "Foo", "1.0");
        using var server = new SlowServer(File.ReadAllBytes(game.MakeZip("Baz", "1.0")));

        var service = game.Mods();
        var first = service.InstallFromUrlAsync(server.Url, ct: TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // 让第一个安装先拿到锁、开始下载

        var foo = service.Scan().Single(m => m.Id == "Foo");

        var secondZip = game.Mods().InstallFromZip(game.MakeZip("Qux", "1.0"));
        var secondUrl = await game.Mods().InstallFromUrlAsync(server.Url, ct: TestContext.Current.CancellationToken);
        var uninstall = service.Uninstall(foo);
        var toggle = service.SetEnabled(foo, false);

        Assert.False(secondZip.Success);
        Assert.Contains("正在进行", secondZip.Message);
        Assert.False(secondUrl.Success);
        Assert.False(uninstall.Success);
        Assert.False(toggle.Success);
        Assert.True(Directory.Exists(Path.Combine(game.ModsPath, "Foo")));

        server.Release();
        var done = await first;

        Assert.True(done.Success, done.Message);
        Assert.True(Directory.Exists(Path.Combine(game.ModsPath, "Baz")));
        Assert.True(game.Mods().InstallFromZip(game.MakeZip("Qux", "1.0")).Success);
    }

    /// <summary>安装进行中扫描列表：不能清理正在使用的临时目录（拿不到锁就跳过这次清理）。</summary>
    [Fact]
    public async Task Scan_DuringInstall_DoesNotTouchTempFolders()
    {
        using var game = new TestGame();
        using var server = new SlowServer(File.ReadAllBytes(game.MakeZip("Baz", "1.0")));
        var inUse = Path.Combine(game.ModsPath, ".aurora-new-inuse");
        Directory.CreateDirectory(inUse);

        var install = game.Mods().InstallFromUrlAsync(server.Url, ct: TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        game.Mods().Scan();
        Assert.True(Directory.Exists(inUse));

        server.Release();
        Assert.True((await install).Success);

        // 操作结束后，下一次扫描照常清理
        game.Mods().Scan();
        Assert.False(Directory.Exists(inUse));
    }
}
