using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Services;
using AdofaiModManager.Services.Sources;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Dialogs;

/// <summary>
/// 首次使用向导：① 游戏目录 → ② UMM 加载器 → ③ 资源站（可选）。
/// 每一步都可以跳过；完成后设置 HasCompletedSetup，之后可在「设置」里重新打开。
/// </summary>
public partial class FirstRunWizard : FluentWindow
{
    private int _step = 1;

    /// <summary>第一次使用：资源站默认只勾 ADOFAITools（用户动过就不再套默认值）。</summary>
    private readonly bool _applyDefaultSources;

    private bool _sourcesTouched;

    private bool _syncingSources;

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
        _applyDefaultSources = !settings.HasCompletedSetup;

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
        else if (step == 2)
        {
            // 加载器：进这一步时检测状态，并查一下资源站最新版本
            RefreshLoaderStatus();
        }
        else if (step == 3)
        {
            RefreshSourceStep();
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

        if (status.IsInstalled)
        {
            LoaderStatusText.Text =
                "✓ 加载器已就绪，可以直接下一步。\n"
                + $"已部署版本：{status.LoaderVersion ?? "未知"}\n"
                + "注入方式：UnityDoorstop / DoorstopProxy";
        }
        else if (status.IsPartial)
        {
            LoaderStatusText.Text = "⚠ 检测到加载器不完整，建议重新安装一次。";
        }
        else
        {
            LoaderStatusText.Text = "○ 尚未安装加载器。从下面三种方式里选一个安装即可。";
        }

        _ = UpdateSiteKernelInfoAsync();
    }

    private void SetKernelButtonsEnabled(bool enabled)
    {
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

        var client = AppServices.CreateApiClient();
        if (client is null || !client.HasKey)
        {
            SiteKernelText.Text = $"（没有可用的 API key，不查询站点）　Aurora 内置内核：{bundledText}";

            if (bundled is null)
            {
                Recommend(KernelChoice.None, "内置内核缺失，请重新安装 Aurora，或先在「设置」里填写 ADOFAITools 的 key。");
            }
            else
            {
                Recommend(KernelChoice.Bundled,
                    $"没有可用的 ADOFAITools key，推荐直接用内置内核（{bundledText}）——不用联网。");
            }

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
            Report(result.Success, result.Message);
        }
        finally
        {
            SetKernelButtonsEnabled(true);
            RefreshLoaderStatus();
        }
    }

    // ---------------- 第 3 步：资源站 ----------------

    private void RefreshSourceStep()
    {
        var custom = AdofaiToolsSource.CreateCustomSite();

        _syncingSources = true;
        try
        {
            WizardSiteCheck.Content = AppServices.SiteDisplayName;
            WizardCustomCheck.Visibility = custom is null ? Visibility.Collapsed : Visibility.Visible;

            if (_applyDefaultSources && !_sourcesTouched)
            {
                WizardSiteCheck.IsChecked = true;
                WizardModriftCheck.IsChecked = false;
                WizardTufCheck.IsChecked = false;
                WizardModlistCheck.IsChecked = false;
                WizardCustomCheck.IsChecked = false;
                WizardSourceHint.Text = "默认只启用 ADOFAITools；需要其它来源时直接勾上即可。";
            }
            else
            {
                WizardSiteCheck.IsChecked = RemoteSources.IsSiteEnabled();
                WizardModriftCheck.IsChecked = RemoteSources.IsEnabled("modrift");
                WizardTufCheck.IsChecked = RemoteSources.IsEnabled("tuf");
                WizardModlistCheck.IsChecked = RemoteSources.IsEnabled("modlist");
                WizardCustomCheck.IsChecked = custom is not null && RemoteSources.IsEnabled(custom.Id);
                WizardSourceHint.Text = string.Empty;
            }
        }
        finally
        {
            _syncingSources = false;
        }
    }

    private (CheckBox Box, string Id)[] SourceCheckboxes()
    {
        var custom = AdofaiToolsSource.CreateCustomSite();
        return
        [
            (WizardSiteCheck, RemoteSources.SiteId),
            (WizardModriftCheck, "modrift"),
            (WizardTufCheck, "tuf"),
            (WizardModlistCheck, "modlist"),
            (WizardCustomCheck, custom?.Id ?? "custom"),
        ];
    }

    private void WizardSource_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSources)
        {
            return;
        }

        var visible = SourceCheckboxes().Where(m => m.Box.Visibility == Visibility.Visible).ToList();

        if (visible.All(m => m.Box.IsChecked != true))
        {
            if (sender is CheckBox clicked)
            {
                _syncingSources = true;
                clicked.IsChecked = true;
                _syncingSources = false;
            }

            WizardSourceHint.Text = "至少要保留一个来源。";
            return;
        }

        SaveSourceSelection(visible);
        _sourcesTouched = true;
        WizardSourceHint.Text = "已保存。以后可以在「设置 → 启用的来源」里修改。";
    }

    private static void SaveSourceSelection(IEnumerable<(CheckBox Box, string Id)> items)
    {
        var disabled = AppServices.Settings.Settings.DisabledSources;
        disabled.Clear();
        foreach (var (box, id) in items)
        {
            if (box.IsChecked != true)
            {
                disabled.Add(id);
            }
        }

        AppServices.Settings.Save();
    }

    /// <summary>第一次使用且没动过第 3 步时：默认只留 ADOFAITools。</summary>
    private static void ApplyDefaultSources()
    {
        var disabled = AppServices.Settings.Settings.DisabledSources;
        disabled.Clear();
        disabled.Add("modrift");
        disabled.Add("tuf");
        disabled.Add("modlist");
        if (AdofaiToolsSource.CreateCustomSite() is { } custom)
        {
            disabled.Add(custom.Id);
        }

        AppServices.Settings.Save();
    }

    private async void TestWizardApi_Click(object sender, RoutedEventArgs e)
    {
        var client = AppServices.CreateApiClient();
        if (client is null)
        {
            WizardApiStatusText.Text = "✗ 尚未配置 ADOFAITools 地址。";
            return;
        }

        WizardApiStatusText.Text = "正在测试连接…";

        try
        {
            var page = await client.GetModsAsync(1, 1);
            WizardApiStatusText.Text = $"✓ 连接成功，ADOFAITools 共有 {page.Total} 个资源。";
        }
        catch (AdofaiToolsException ex)
        {
            WizardApiStatusText.Text = "✗ " + ex.Message;
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
        // 第一次使用、且用户没在第 3 步改过：默认只启用 ADOFAITools
        if (_applyDefaultSources && !_sourcesTouched)
        {
            ApplyDefaultSources();
        }

        AppServices.Settings.Settings.HasCompletedSetup = true;
        AppServices.Settings.Save();
        DialogResult = true;
    }
}
