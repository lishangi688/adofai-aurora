using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Dialogs;

/// <summary>
/// 首次使用向导：① 游戏目录 → ② UMM 加载器 → ③ 资源站（可选）。
/// 每一步都可以跳过；完成后设置 HasCompletedSetup，之后可在「设置」里重新打开。
/// </summary>
public partial class FirstRunWizard : FluentWindow
{
    private int _step = 1;
    private bool _loaderPrompted;

    public FirstRunWizard()
    {
        InitializeComponent();

        var settings = AppServices.Settings.Settings;

        // 游戏目录：没设置过就自动检测
        if (string.IsNullOrWhiteSpace(settings.GamePath))
        {
            var best = AppServices.GameLocator.FindBest();
            if (best is not null)
            {
                settings.GamePath = best;
                AppServices.Settings.Save();
            }
        }

        GamePathBox.Text = settings.GamePath ?? string.Empty;
        ApiBaseBox.Text = settings.ApiBaseUrl;
        ApiKeyBox.Password = settings.ApiKey ?? string.Empty;

        SetStep(1);
    }

    private static LoaderService? CreateLoader()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        return new LoaderService(gamePath!);
    }

    private void SetStep(int step)
    {
        _step = step;

        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        Step1Label.Opacity = step == 1 ? 1 : 0.4;
        Step2Label.Opacity = step == 2 ? 1 : 0.4;
        Step3Label.Opacity = step == 3 ? 1 : 0.4;

        Step1Label.FontWeight = step == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        Step2Label.FontWeight = step == 2 ? FontWeights.SemiBold : FontWeights.Normal;
        Step3Label.FontWeight = step == 3 ? FontWeights.SemiBold : FontWeights.Normal;

        BackButton.IsEnabled = step > 1;
        NextButton.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        SkipStepButton.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed;

        StatusBar.IsOpen = false;

        if (step == 1)
        {
            UpdateGameStatus();
        }
        else if (step == 3)
        {
            // 第 3 步是加载器：进这一步时检测状态，并查一下资源站最新版本
            RefreshLoaderStatus();

            if (!_loaderPrompted)
            {
                _loaderPrompted = true;
                Dispatcher.BeginInvoke(new Action(PromptLoaderSelectionIfNeeded));
            }
        }
    }

    private void Report(bool success, string message)
    {
        StatusBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = success ? "提示" : "出错了";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // ---------------- 第 1 步：游戏目录 ----------------

    private void GamePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AppServices.Settings.Settings.GamePath = GamePathBox.Text.Trim();
        AppServices.Settings.Save();
        UpdateGameStatus();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var best = AppServices.GameLocator.FindBest();
        if (best is null)
        {
            Report(false, "未能在 Steam 库中找到《冰与火之舞》，请手动选择游戏目录。");
            return;
        }

        GamePathBox.Text = best;
        Report(true, "已自动检测到游戏目录。");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择《冰与火之舞》游戏目录" };
        if (dialog.ShowDialog() == true)
        {
            GamePathBox.Text = dialog.FolderName;
        }
    }

    private void UpdateGameStatus()
    {
        var path = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            GameStatusText.Text = "尚未设置游戏目录。";
            return;
        }

        if (!Directory.Exists(path))
        {
            GameStatusText.Text = "⚠ 该目录不存在。";
            return;
        }

        var hasExe = File.Exists(Path.Combine(path, SteamGameLocator.GameExeName));
        var hasMods = Directory.Exists(Path.Combine(path, "Mods"));

        GameStatusText.Text = hasExe
            ? $"✓ 已找到游戏主程序。Mods 目录：{(hasMods ? "存在" : "不存在（装过 mod 后会出现）")}"
            : "⚠ 该目录里没找到游戏主程序，请确认路径是否正确。";
    }

    // ---------------- 第 2 步：加载器 ----------------

    private void RefreshLoaderStatus()
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            LoaderStatusText.Text = "⚠ 尚未设置游戏目录，请回到第 1 步。";
            SetKernelButtonsEnabled(false);
            return;
        }

        var status = loader.Detect();
        if (!status.GameExists)
        {
            LoaderStatusText.Text = "⚠ 找不到游戏主程序，请回到第 1 步检查目录。";
            SetKernelButtonsEnabled(false);
            return;
        }

        SetKernelButtonsEnabled(true);

        var environment = GameLoaderEnvironment.Detect(loader.GamePath);
        var otherInjection = environment.ActiveInjection is not (ModLoaderKind.UnityModManager or ModLoaderKind.Unknown);
        InstallFromSiteButton.IsEnabled = !otherInjection;
        InstallBundledButton.IsEnabled = !otherInjection;
        ImportKernelButton.IsEnabled = !otherInjection;

        if (environment.HasAnyLoader)
        {
            var parts = new List<string>
            {
                environment.ActiveInjection == ModLoaderKind.Unknown
                    ? "已安装加载器，但没有检测到有效注入"
                    : $"当前注入：{environment.ActiveInjection.DisplayName()}",
            };

            if (environment.UmmLoaderInstalled)
            {
                parts.Add($"UnityModManager {environment.UmmVersion ?? "未知"}（mod：{environment.UmmModsPath}）");
            }

            if (environment.MelonLoaderInstalled)
            {
                parts.Add($"MelonLoader {environment.MelonLoaderVersion ?? "未知"}（mod：{environment.MelonModsPath}）");
            }

            if (environment.BepInExInstalled)
            {
                parts.Add($"BepInEx {environment.BepInExVersion ?? "未知"}（plugins：{environment.BepInExPluginsPath}）");
            }

            if (environment.UmmCompatInstalled)
            {
                parts.Add($"UMMBridge {environment.UmmCompatVersion ?? "未知"}"
                          + (environment.UmmCompatEnabled ? "（已启用）" : "（已禁用）"));
            }

            LoaderStatusText.Text = "✓ " + string.Join("\n", parts);
        }
        else if (status.IsPartial)
        {
            LoaderStatusText.Text = "⚠ 检测到部分加载器文件，建议重新安装一次。";
        }
        else
        {
            LoaderStatusText.Text = "○ 尚未安装加载器。点「选择加载器并安装」选择要用的加载器。";
        }

        _ = UpdateSiteKernelInfoAsync();
    }

    private void PromptLoaderSelectionIfNeeded()
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            return;
        }

        if (GameLoaderEnvironment.Detect(loader.GamePath) is { HasAnyLoader: true, HasInjection: true })
        {
            return;
        }

        ShowLoaderDialog(loader.GamePath);
    }

    private void SelectLoader_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在第 1 步设置游戏目录。");
            return;
        }

        ShowLoaderDialog(loader.GamePath);
    }

    private void ShowLoaderDialog(string gamePath)
    {
        var dialog = new LoaderSelectionDialog(gamePath)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            Report(true, "加载器安装完成。");
            RefreshLoaderStatus();
        }
    }

    private void SetKernelButtonsEnabled(bool enabled)
    {
        SelectLoaderButton.IsEnabled = enabled;
        InstallFromSiteButton.IsEnabled = enabled;
        InstallBundledButton.IsEnabled = enabled;
        ImportKernelButton.IsEnabled = enabled;
    }

    private enum KernelChoice
    {
        None,
        Bundled,
        Site,
    }

    /// <summary>查一下资源站上的最新内核版本，并给出"用哪个"的推荐。</summary>
    private async Task UpdateSiteKernelInfoAsync()
    {
        var loader = CreateLoader();
        var bundled = loader is null ? null : new KernelService(loader).GetBundled();
        var bundledText = bundled?.Version ?? "缺失";

        // 第 2 步没填 API key 时，站点这条路走不通（也下载不了），直接推荐内置
        var hasKey = !string.IsNullOrWhiteSpace(AppServices.Settings.Settings.ApiKey);

        if (!hasKey)
        {
            SiteKernelText.Text = $"（未填写 API key，不查询站点）　Aurora 内置内核：{bundledText}";

            if (bundled is null)
            {
                Recommend(KernelChoice.None, "内置内核缺失，请重新安装 Aurora，或先从 ADOFAITools 配置好 API key 再回来。");
            }
            else
            {
                Recommend(KernelChoice.Bundled,
                    $"没有填写 ADOFAITools 的 API key，推荐直接用内置内核（{bundledText}）——不用登录，也不受网络影响。");
            }

            return;
        }

        var client = AppServices.CreateApiClient();
        if (client is null)
        {
            SiteKernelText.Text = $"（ADOFAITools 地址为空）　Aurora 内置内核：{bundledText}";
            Recommend(KernelChoice.Bundled, $"推荐使用内置内核（{bundledText}）。");
            return;
        }

        SiteKernelText.Text = "ADOFAITools 最新内核：查询中…";

        try
        {
            var site = await KernelBootstrapper.FindLatestSiteKernelAsync(client);

            if (site is null)
            {
                SiteKernelText.Text = $"ADOFAITools 上没有找到 UnityModManager　·　Aurora 内置内核：{bundledText}";
                Recommend(KernelChoice.Bundled, $"站点上没有内核，推荐使用内置内核（{bundledText}）。");
                return;
            }

            var siteVersion = site.Value.VersionId;
            SiteKernelText.Text = $"ADOFAITools 最新内核：{siteVersion}　·　Aurora 内置内核：{bundledText}";

            if (bundled is null)
            {
                Recommend(KernelChoice.Site, $"内置内核缺失，推荐从 ADOFAITools 获取 {siteVersion}。");
                return;
            }

            var diff = KernelService.CompareVersions(siteVersion, bundled.Version);

            if (diff > 0)
            {
                Recommend(KernelChoice.Site,
                    $"站点上的 {siteVersion} 比内置的 {bundledText} 新，推荐从 ADOFAITools 获取最新版。");
            }
            else if (diff == 0)
            {
                Recommend(KernelChoice.Bundled,
                    $"两边都是 {siteVersion}，推荐用内置内核——不用下载，也不受网络影响。");
            }
            else
            {
                Recommend(KernelChoice.Bundled,
                    $"内置的 {bundledText} 比站点的 {siteVersion} 还新，推荐用内置内核。");
            }
        }
        catch (AdofaiToolsException ex)
        {
            SiteKernelText.Text = $"ADOFAITools 暂时不可用（{ex.Message}）　·　可用内置内核：{bundledText}";
            Recommend(KernelChoice.Bundled, $"站点连不上，推荐使用内置内核（{bundledText}）。");
        }
    }

    /// <summary>
    /// 把"推荐哪一个"落到界面上：推荐的那个用主按钮样式 + 文案带「推荐」，
    /// 再用一句话说明为什么（版本对比 / 没填 key / 站点连不上）。
    /// </summary>
    private void Recommend(KernelChoice choice, string reason)
    {
        var bundledRecommended = choice == KernelChoice.Bundled;
        var siteRecommended = choice == KernelChoice.Site;

        InstallBundledButton.Appearance = bundledRecommended ? ControlAppearance.Primary : ControlAppearance.Secondary;
        InstallFromSiteButton.Appearance = siteRecommended ? ControlAppearance.Primary : ControlAppearance.Secondary;

        InstallBundledButton.Content = bundledRecommended ? "使用内置内核（推荐）" : "使用内置内核";
        // 不写「最新」：站点版本可能比内置还旧（点下去等于降级），上面那行已经列出两边版本
        InstallFromSiteButton.Content = siteRecommended ? "从 ADOFAITools 获取（推荐）" : "从 ADOFAITools 获取";

        KernelAdviceText.Text = "💡 " + reason;
        KernelAdviceBox.Visibility = Visibility.Visible;
    }

    private async void InstallFromSite_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在第 1 步设置游戏目录。");
            return;
        }

        var client = AppServices.CreateApiClient();
        if (client is null)
        {
            Report(false, "尚未配置 ADOFAITools 地址，可改用内置内核。");
            return;
        }

        SetKernelButtonsEnabled(false);
        try
        {
            var progress = new InlineProgress<string>(message => Report(true, message), Dispatcher);
            var result = await new KernelBootstrapper(loader).InstallFromSiteAsync(client, progress);
            if (result.Success)
            {
                ModService.InvalidateCache();
            }

            Report(result.Success, result.Message);
        }
        catch (AdofaiToolsException ex)
        {
            Report(false, ex.Message + "　（可改用内置内核）");
        }
        finally
        {
            SetKernelButtonsEnabled(true);
            RefreshLoaderStatus();
        }
    }

    private void InstallBundled_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在第 1 步设置游戏目录。");
            return;
        }

        var result = new KernelBootstrapper(loader).InstallBundled();
        if (result.Success)
        {
            ModService.InvalidateCache();
        }

        Report(result.Success, result.Message);
        RefreshLoaderStatus();
    }

    private async void ImportKernel_Click(object sender, RoutedEventArgs e)
    {
        var loader = CreateLoader();
        if (loader is null)
        {
            Report(false, "请先在第 1 步设置游戏目录。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 UnityModManager 压缩包（可从 Nexus Mods 下载）",
            Filter = "压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SetKernelButtonsEnabled(false);
        try
        {
            var kernel = new KernelService(loader);
            var (imported, message) = await kernel.ImportAsync(dialog.FileName);

            if (imported is null)
            {
                Report(false, message);
                return;
            }

            var result = kernel.Deploy(imported);
            if (result.Success)
            {
                ModService.InvalidateCache();
            }

            Report(result.Success, result.Message);
        }
        finally
        {
            SetKernelButtonsEnabled(true);
            RefreshLoaderStatus();
        }
    }

    // ---------------- 第 3 步：资源站（可选） ----------------

    private void ApiBaseBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AppServices.Settings.Settings.ApiBaseUrl = ApiBaseBox.Text.Trim();
        AppServices.Settings.Save();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.Settings.ApiKey = ApiKeyBox.Password.Trim();
        AppServices.Settings.Save();
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        var client = AppServices.CreateApiClient();
        if (client is null)
        {
            ApiStatusText.Text = "✗ 请先填写 ADOFAITools 地址。";
            return;
        }

        ApiStatusText.Text = "正在测试连接…";

        try
        {
            var page = await client.GetModsAsync(1, 1);
            ApiStatusText.Text = $"✓ 连接成功，ADOFAITools 共有 {page.Total} 个资源。";
        }
        catch (AdofaiToolsException ex)
        {
            ApiStatusText.Text = "✗ " + ex.Message;
        }
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        var url = AppServices.Settings.Settings.ApiBaseUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 导航 ----------------

    private void Back_Click(object sender, RoutedEventArgs e) => SetStep(Math.Max(1, _step - 1));

    private void Next_Click(object sender, RoutedEventArgs e) => SetStep(Math.Min(3, _step + 1));

    private void SkipStep_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            SetStep(_step + 1);
        }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.Settings.HasCompletedSetup = true;
        AppServices.Settings.Save();
        DialogResult = true;
    }
}
