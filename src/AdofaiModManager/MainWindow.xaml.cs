using System.IO;
using System.Windows;
using AdofaiModManager.Services;
using AdofaiModManager.Views.Pages;
using Wpf.Ui.Controls;

namespace AdofaiModManager;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        RootNavigation.SetPageProviderService(
            new NavigationViewPageProvider(type => Activator.CreateInstance(type)));

        AppServices.Updates.Changed += OnUpdatesChanged;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // 打开上次停留的页面；新装 / 认不出时默认「已安装」（最常用）
        var page = AppServices.Settings.Settings.LastPage switch
        {
            nameof(OnlineModsPage) => typeof(OnlineModsPage),
            nameof(FavoritesPage) => typeof(FavoritesPage),
            nameof(LoaderPage) => typeof(LoaderPage),
            nameof(SettingsPage) => typeof(SettingsPage),
            _ => typeof(InstalledModsPage),
        };

        RootNavigation.Navigate(page, null);

        if (AppServices.Settings.Settings.CheckUpdatesOnStartup)
        {
            // 先查 Aurora 自身（GitHub + 资源站，通常两秒内出结果），提示条能立刻出现；
            // 再查已安装 mod 的更新 —— 那一步要跑各个来源，慢的时候要几十秒。
            await RunStartupAppUpdateCheckAsync();
            await RunStartupUpdateCheckAsync();
        }
    }

    private AppUpdateInfo? _appUpdate;

    /// <summary>启动时静默检查 Aurora 自身有没有新版本（有新版本才显示提示条）。</summary>
    private async Task RunStartupAppUpdateCheckAsync()
    {
        try
        {
            var info = await AppUpdateService.CheckAsync();
            if (info is null)
            {
                AppPaths.AppendDebugLog("[appupdate] 检查结果：没有新版本");
                return;
            }

            AppPaths.AppendDebugLog($"[appupdate] 发现新版本 {info.Version}（来源 {info.SourceLabel}）");

            // 用户点过「忽略此版本」就不再提示（换新版本还会提示）
            if (string.Equals(
                    AppServices.Settings.Settings.SkippedAppVersion,
                    info.Version,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _appUpdate = info;
            AppUpdateText.Text =
                $"Aurora 有新版本 v{info.Version}（来源：{info.SourceLabel}）　·　当前 v{AppUpdateService.CurrentVersion}"
                + $"\n{AppUpdateService.DistributionHint}";
            AppUpdateBar.Visibility = Visibility.Visible;
        }
        catch
        {
            // 自身更新检查失败不打扰用户
        }
    }

    private void OpenAppRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdate is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_appUpdate.PageUrl) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败就算了
        }

        AppUpdateBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>下载新版本并自动替换（绿色版解压覆盖；安装版调用安装程序）。</summary>
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdate is null)
        {
            return;
        }

        UpdateNowButton.IsEnabled = false;
        OpenReleaseButton.IsEnabled = false;
        SkipVersionButton.IsEnabled = false;

        try
        {
            var progress = new InlineProgress<string>(
                text => AppUpdateText.Text = text,
                Dispatcher);

            var result = await AppSelfUpdater.ApplyAsync(
                _appUpdate,
                AppUpdateService.Distribution,
                progress);

            AppUpdateText.Text = result.Message;

            if (result.ShouldExit)
            {
                await Task.Delay(1500);

                // 退出程序，把"替换文件"交给更新脚本 / 安装程序
                Application.Current.Shutdown();
                return;
            }
        }
        finally
        {
            UpdateNowButton.IsEnabled = true;
            OpenReleaseButton.IsEnabled = true;
            SkipVersionButton.IsEnabled = true;
        }
    }

    private void SkipAppVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdate is not null)
        {
            AppServices.Settings.Settings.SkippedAppVersion = _appUpdate.Version;
            AppServices.Settings.Save();
        }

        AppUpdateBar.Visibility = Visibility.Collapsed;
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            var gamePath = AppServices.Settings.Settings.GamePath;
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                return;
            }

            var mods = ModService.Get(gamePath).Scan();
            if (mods.Count == 0)
            {
                return;
            }

            await AppServices.Updates.CheckAllAsync(mods);
        }
        catch
        {
            // 启动检查失败不影响使用
        }
    }

    private void OnUpdatesChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateBadge);
            return;
        }

        UpdateBadge();
    }

    private void UpdateBadge()
    {
        var count = AppServices.Updates.UpdatableCount;

        InstalledNavItem.InfoBadge = count > 0
            ? new InfoBadge { Value = count.ToString(), Severity = InfoBadgeSeverity.Attention }
            : null;
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is INavigationViewItem item && item.TargetPageType is { } pageType)
        {
            sender.Navigate(pageType, null);

            AppServices.Settings.Settings.LastPage = pageType.Name;
            AppServices.Settings.Save();
        }
    }
}
