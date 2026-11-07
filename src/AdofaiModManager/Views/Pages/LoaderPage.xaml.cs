using AdofaiModManager.Models;
using AdofaiModManager.Services;
using AdofaiModManager.Views.Dialogs;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Pages;

public partial class LoaderPage : Page
{
    private bool _busy;

    public LoaderPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PageScrollFix.DisableOuterPageScrolling(this);
            Refresh();
        };
    }

    private LoaderService? CreateLoader()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        return new LoaderService(gamePath);
    }

    private void Refresh()
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            StateText.Text = "⚠ 尚未设置游戏目录";
            DetailText.Text = "请到「设置」里指定《冰与火之舞》的安装目录。";
            InstallButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            RollbackButton.IsEnabled = false;
            CheckUpdateButton.IsEnabled = false;
            return;
        }

        var status = loader.Detect();
        var kernel = new KernelService(loader);
        var deployed = kernel.GetDeployedVersion();
        var kernels = kernel.GetLocalKernels();
        var bundled = kernel.GetBundled();

        if (!status.GameExists)
        {
            StateText.Text = "⚠ 找不到游戏主程序";
            DetailText.Text = $"路径：{loader.ExecutablePath}";
            InstallButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
            RollbackButton.IsEnabled = false;
            CheckUpdateButton.IsEnabled = false;
            return;
        }

        var lines = new List<string>
        {
            $"游戏目录：{loader.GamePath}",
            $"游戏架构：{(status.Is64BitGame ? "64 位" : "32 位")}",
            $"Mods 目录：{(Directory.Exists(Path.Combine(loader.GamePath, "Mods")) ? "存在" : "不存在")}",
            string.Empty,
            $"内置内核：{(bundled is null ? "缺失" : bundled.Version)}",
            $"已部署内核：{deployed ?? "未部署"}",
        };

        if (kernels.Count > 1)
        {
            lines.Add("本地内核包：" + string.Join("、", kernels.Select(k => $"{k.Version}({k.Source})")));
        }

        // 内置内核比已部署的新时要明确提示：
        // 以前只拿"资源站版本"和已部署版本比，而资源站上的内核归站长维护、不一定跟着更新，
        // 于是软件自己升级了内置内核也不会提示。
        if (bundled is not null && deployed is not null &&
            KernelService.CompareVersions(bundled.Version, deployed) > 0)
        {
            lines.Add($"⚠ 内置内核 {bundled.Version} 比已部署的 {deployed} 新："
                      + "点「安装 / 修复加载器」→「使用内置内核」即可更新。");
        }

        if (status.IsInstalled)
        {
            StateText.Text = "✓ 加载器已安装（DoorstopProxy）";
            lines.Insert(3, $"注入方式：UnityDoorstop / DoorstopProxy    winhttp.dll：已部署    doorstop_config.ini：已部署");
        }
        else if (status.IsPartial)
        {
            StateText.Text = "⚠ 加载器不完整";
            lines.Insert(1, "检测到部分加载器文件，建议点「安装 / 修复加载器」补齐。");
        }
        else
        {
            StateText.Text = "○ 尚未安装加载器";
            lines.Insert(1, "点击「安装 / 修复加载器」即可为游戏装上 mod 支持。");
        }

        if (status.LegacyBackups.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"⚠ 检测到 {status.LegacyBackups.Length} 个旧版注入残留（Assembly 方式）："
                + string.Join("、", status.LegacyBackups.Select(Path.GetFileName)));
            lines.Add("建议先卸载加载器清理，再重新安装。");
        }

        DetailText.Text = string.Join(Environment.NewLine, lines);

        var hasAnything = status.LoaderFolderExists || status.WinhttpExists || status.DoorstopConfigExists;

        InstallButton.IsEnabled = !_busy;
        UninstallButton.IsEnabled = !_busy && hasAnything;
        RollbackButton.IsEnabled = !_busy && kernels.Count > 1;
        CheckUpdateButton.IsEnabled = !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        RollbackButton.IsEnabled = !busy;
        CheckUpdateButton.IsEnabled = !busy;
    }

    private void Report(InstallResult result)
    {
        StatusBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = result.Success ? "操作成功" : "操作失败";
        StatusBar.Message = result.Message;
        StatusBar.IsOpen = true;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        var choice = new KernelSourceDialog { Owner = Window.GetWindow(this) };
        if (choice.ShowDialog() != true)
        {
            return;
        }

        if (choice.Choice == KernelSourceChoice.ImportZip)
        {
            await ImportKernelZipAsync(loader);
        }
        else if (choice.Choice == KernelSourceChoice.UseBundled)
        {
            SetBusy(true);
            try
            {
                var result = new KernelBootstrapper(loader).InstallBundled();
                Report(result.Success, result.Message);
            }
            finally
            {
                SetBusy(false);
                Refresh();
            }
        }
        else
        {
            await FetchOrBundledAsync(loader);
        }
    }

    /// <summary>从资源站取最新内核；没有更新或失败时用内置内核。</summary>
    private async Task FetchOrBundledAsync(LoaderService loader)
    {
        SetBusy(true);
        try
        {
            // 优先用资源站上更新的内核，否则用内置内核
            var client = BuildClient(out _);
            var progress = new InlineProgress<string>(message => Report(true, message), Dispatcher);

            var result = await new KernelBootstrapper(loader).InstallBestAsync(client, progress);
            Report(result.Success, result.Message);
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    /// <summary>从资源站检查内核是否有更新（只查询，不改动）。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        // 站点不可用（没填 key / 连不上）时，仍然可以用内置内核做比较
        var client = BuildClient(out _);

        var kernel = new KernelService(loader);
        SetBusy(true);

        try
        {
            var current = kernel.GetDeployedVersion();
            var bundled = kernel.GetBundled();

            Report(true, "正在检查内核更新…");

            // 资源站上的内核归站长维护，不一定会跟着软件一起更新；
            // 所以这里同时比较「站点版本」和「内置版本」，取更高的那个。
            // 没有 API key / 站点连不上时，也能只靠内置内核给出判断。
            (string VersionId, string FileId)? site = null;
            if (client is not null)
            {
                try
                {
                    site = await KernelBootstrapper.FindLatestSiteKernelAsync(client);
                }
                catch (AdofaiToolsException)
                {
                    // 站点不可用：继续用内置内核比
                }
            }

            var siteVersion = site?.VersionId;
            var bundledVersion = bundled?.Version;

            var bestVersion = siteVersion;
            var bestSource = "ADOFAITools";
            var useBundled = false;

            if (bundledVersion is not null &&
                (siteVersion is null || KernelService.CompareVersions(bundledVersion, siteVersion) > 0))
            {
                bestVersion = bundledVersion;
                bestSource = "内置内核";
                useBundled = true;
            }

            if (bestVersion is null)
            {
                Report(false, "拿不到内核版本信息（站点不可用，内置内核也缺失）。");
                return;
            }

            var summary = $"站点 {siteVersion ?? "无"}　·　内置 {bundledVersion ?? "无"}　·　已部署 {current ?? "未部署"}";

            if (current is not null && KernelService.CompareVersions(bestVersion, current) <= 0)
            {
                Report(true, $"内核已是最新（{summary}）。");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"{bestSource}有更新的内核 {bestVersion}（当前：{current ?? "未部署"}）。\n\n是否更新？\n（升级前会自动备份当前内核，可回滚）",
                "内核更新",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            if (useBundled)
            {
                // 用内置的：不需要下载
                Report(new KernelBootstrapper(loader).InstallBundled());
            }
            else
            {
                var intent = await client!.CreateToolDownloadIntentAsync(site!.Value.FileId);
                var (imported, message) = await kernel.ImportAsync(intent.Url, site.Value.VersionId);
                if (imported is null)
                {
                    Report(false, message);
                    return;
                }

                Report(kernel.Deploy(imported));
            }
        }
        catch (AdofaiToolsException ex)
        {
            Report(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            return;
        }

        var kernel = new KernelService(loader);
        var current = kernel.GetDeployedVersion();

        var confirm = System.Windows.MessageBox.Show(
            $"确定要回滚内核吗？\n当前已部署：{current ?? "未部署"}\n\n会切换到本地保存的上一个内核版本（同样会先备份当前版本）。",
            "回滚内核",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        Report(kernel.Rollback());
        Refresh();
    }

    private void Report(bool success, string message)
    {
        StatusBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = success ? "提示" : "出错了";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static AdofaiToolsClient? BuildClient(out string error)
    {
        error = string.Empty;
        var client = AppServices.CreateApiClient();

        if (client is null)
        {
            error = "尚未配置 ADOFAITools 地址。请到「设置」里填写。";
        }

        return client;
    }

    /// <summary>导入本地 UnityModManager 压缩包（例如从 Nexus Mods 下载的）。</summary>
    private async Task ImportKernelZipAsync(LoaderService loader)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 UnityModManager 压缩包（可从 Nexus Mods 下载）",
            Filter = "压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var kernel = new KernelService(loader);
        SetBusy(true);

        try
        {
            Report(true, "正在导入内核…");
            var (imported, message) = await kernel.ImportAsync(dialog.FileName, allowLocalFile: true);
            if (imported is null)
            {
                Report(false, message);
                return;
            }

            Report(kernel.Deploy(imported));
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)    {
        var loader = CreateLoader();
        if (loader is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            "确定要卸载游戏加载器吗？\n这会移除 winhttp.dll、doorstop_config.ini 和 UnityModManager 目录。\n（Mods 文件夹里的 mod 不会被删除）",
            "确认卸载加载器",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        Report(loader.Uninstall());
        Refresh();
    }

    private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            return;
        }

        try
        {
            PathToExplorer.Open(loader.GamePath);
        }
        catch (InvalidPathException ex)
        {
            AppPaths.AppendDebugLog($"[open] 无效路径: {loader.GamePath}");
            Report(new InstallResult(false, $"游戏目录路径包含无效字符，无法打开（{ex.Message}）。请到「设置」里检查游戏目录。"));
        }
        catch (Exception ex)
        {
            Report(new InstallResult(false, $"打开目录失败：{ex.Message}"));
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(new InstallResult(false, "请先在「设置」里指定游戏目录。"));
            return;
        }

        try
        {
            loader.LaunchGame();
            Report(new InstallResult(true, "已请求通过 Steam 启动《冰与火之舞》，请稍候。"));
        }
        catch (Exception ex)
        {
            Report(new InstallResult(false, $"启动失败：{ex.Message}"));
        }
    }

    /// <summary>直接运行游戏 exe（不经 Steam）：更快，但成就/云存档可能不同步。</summary>
    private void LaunchDirect_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(new InstallResult(false, "请先在「设置」里指定游戏目录。"));
            return;
        }

        try
        {
            loader.LaunchGameDirectly();
            Report(new InstallResult(true, "已直接启动游戏（未经 Steam）。成就 / 云存档可能不同步。"));
        }
        catch (Exception ex)
        {
            Report(new InstallResult(false, $"启动失败：{ex.Message}"));
        }
    }
}
