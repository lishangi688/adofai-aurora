using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using AdofaiModManager.Views.Dialogs;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Pages;

public partial class LoaderPage : Page
{
    private bool _busy;
    private bool _autoPrompted;

    public LoaderPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PageScrollFix.DisableOuterPageScrolling(this);
            Refresh();

            // 首次进入且完全没有加载器时，直接弹出加载器选择框（类似 PCL）。
            if (!_autoPrompted)
            {
                _autoPrompted = true;
                Dispatcher.BeginInvoke(new Action(PromptLoaderSelectionIfNeeded));
            }
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

    private void PromptLoaderSelectionIfNeeded()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return;
        }

        if (ModService.DetectEnvironment(gamePath) is { HasAnyLoader: true, HasInjection: true })
        {
            return;
        }

        OpenLoaderDialog(gamePath);
    }

    private void OpenLoaderDialog(string gamePath)
    {
        var dialog = new LoaderSelectionDialog(gamePath)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true)
        {
            Report(new InstallResult(true, "加载器安装完成。"));
            Refresh();
        }
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
        var environment = ModService.DetectEnvironment(loader.GamePath);
        var kernels = new KernelService(loader).GetLocalKernels();

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
            $"当前加载器：{(environment.ActiveInjection == ModLoaderKind.Unknown ? "未检测到" : environment.ActiveInjection.DisplayName())}",
            string.Empty,
            $"UnityModManager：{FormatLoader(environment.UmmLoaderInstalled, environment.UmmVersion, environment.ActiveInjection == ModLoaderKind.UnityModManager)}",
            $"MelonLoader：{FormatLoader(environment.MelonLoaderInstalled, environment.MelonLoaderVersion, environment.ActiveInjection == ModLoaderKind.MelonLoader)}",
            $"BepInEx：{FormatLoader(environment.BepInExInstalled, environment.BepInExVersion, environment.ActiveInjection == ModLoaderKind.BepInEx)}",
            $"UMMBridge：{(environment.UmmCompatInstalled ? (environment.UmmCompatEnabled ? $"{environment.UmmCompatVersion ?? "已安装"}（已启用）" : "已安装（已禁用）") : "未安装")}",
            $"UMM mod 目录：{environment.UmmModsPath}",
        };

        if (environment.ActiveInjection != ModLoaderKind.Unknown)
        {
            StateText.Text = $"✓ 当前注入：{environment.ActiveInjection.DisplayName()}";
        }
        else if (environment.HasAnyLoader)
        {
            StateText.Text = "⚠ 已安装加载器，但没有检测到有效注入";
            lines.Insert(1, "加载器文件可能不完整，建议重新安装。");
        }
        else
        {
            StateText.Text = "○ 尚未安装加载器";
            lines.Insert(1, "点击「安装 / 切换加载器」选择要使用的加载器。");
        }

        if (status.LegacyBackups.Length > 0)
        {
            lines.Add($"⚠ 检测到 {status.LegacyBackups.Length} 个旧版注入残留（Assembly 方式），建议卸载 UMM 注入后重装。");
        }

        DetailText.Text = string.Join(Environment.NewLine, lines);

        var otherInjection = environment.ActiveInjection is not (ModLoaderKind.UnityModManager or ModLoaderKind.Unknown);

        InstallButton.IsEnabled = !_busy;
        UninstallButton.IsEnabled = !_busy && environment.ActiveInjection == ModLoaderKind.UnityModManager;
        RollbackButton.IsEnabled = !_busy && !otherInjection && kernels.Count > 1 && environment.UmmLoaderInstalled;
        CheckUpdateButton.IsEnabled = !_busy && !otherInjection;
    }

    private static string FormatLoader(bool installed, string? version, bool active)
    {
        if (!installed)
        {
            return "未安装";
        }

        return $"{version ?? "已安装"}{(active ? "（当前）" : "（未启用）")}";
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

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        OpenLoaderDialog(loader.GamePath);
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
            if (result.Success)
            {
                // 加载器变了 → mod 目录与归属全变，别再用缓存里的环境 / 扫描结果。
                ModService.InvalidateCache();
            }

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

        var environment = ModService.DetectEnvironment(loader.GamePath);
        if (environment.ActiveInjection is not (ModLoaderKind.UnityModManager or ModLoaderKind.Unknown))
        {
            Report(false, "当前注入的是 MelonLoader / BepInEx，不能直接更新 UMM 内核。请用「安装 / 切换加载器」重新安装 UMMBridge 或选择 UMM 单独使用。");
            return;
        }

        var client = BuildClient(out var error);
        if (client is null)
        {
            Report(false, error);
            return;
        }

        var kernel = new KernelService(loader);
        SetBusy(true);

        try
        {
            var current = kernel.GetDeployedVersion() ?? kernel.GetBundled()?.Version;

            Report(true, "正在从 ADOFAITools 检查内核更新…");
            var site = await KernelBootstrapper.FindLatestSiteKernelAsync(client);

            if (site is null)
            {
                Report(false, "ADOFAITools 上没有找到 UnityModManager。");
                return;
            }

            if (current is not null && KernelService.CompareVersions(site.Value.VersionId, current) <= 0)
            {
                Report(true, $"内核已是最新：当前 {current}，ADOFAITools 最新 {site.Value.VersionId}。");
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"ADOFAITools 有更新的内核 {site.Value.VersionId}（当前：{current ?? "未部署"}）。\n\n是否下载并更新？\n（升级前会自动备份当前内核，可回滚）",
                "内核更新",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            var intent = await client.CreateToolDownloadIntentAsync(site.Value.FileId);
            var (imported, message) = await kernel.ImportAsync(intent.Url, site.Value.VersionId);
            if (imported is null)
            {
                Report(false, message);
                return;
            }

            Report(kernel.Deploy(imported));
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

        var environment = ModService.DetectEnvironment(loader.GamePath);
        if (environment.ActiveInjection is not (ModLoaderKind.UnityModManager or ModLoaderKind.Unknown))
        {
            Report(false, "当前注入的是 MelonLoader / BepInEx，不能在这里回滚 UMM 内核。请用「安装 / 切换加载器」重新安装。");
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
            var (imported, message) = await kernel.ImportAsync(dialog.FileName);
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

        var environment = ModService.DetectEnvironment(loader.GamePath);
        if (environment.ActiveInjection != ModLoaderKind.UnityModManager)
        {
            Report(false, "当前注入的不是 UnityModManager。请用「安装 / 切换加载器」选择要保留的环境，Aurora 会先备份旧的注入文件。");
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            "确定要卸载 UnityModManager 注入吗？\n这会移除 winhttp.dll、doorstop_config.ini 和 UnityModManager 目录。\n（Mods / UMMMods 里的 mod 不会被删除）",
            "确认卸载 UMM 注入",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var uninstalled = loader.Uninstall();
        if (uninstalled.Success)
        {
            ModService.InvalidateCache();
        }

        Report(uninstalled);
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
            // 不走 explorer.exe 命令行：带引号的路径参数经常被忽略，会打开「文档」而不是游戏目录。
            ShellExplorer.OpenFolder(loader.GamePath);
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
