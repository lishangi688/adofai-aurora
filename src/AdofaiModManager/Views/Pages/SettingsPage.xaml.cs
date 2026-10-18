using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Services;
using Microsoft.Win32;

namespace AdofaiModManager.Views.Pages;

public partial class SettingsPage : Page
{
    /// <summary>
    /// 是否在设置里显示「重新运行首次使用向导」。
    ///
    /// 公开发布版本**默认不显示**（普通用户不需要，向导只在首次启动出现）。
    /// 开发调试或录制素材时，设置环境变量 <c>AMM_SHOW_WIZARD_BUTTON=1</c> 启动即可打开，
    /// 这样就不需要改代码、也不会把调试开关误提交进仓库。
    /// </summary>
    private static readonly bool ShowRerunWizard =
        string.Equals(
            Environment.GetEnvironmentVariable("AMM_SHOW_WIZARD_BUTTON"),
            "1",
            StringComparison.Ordinal);

    private readonly bool _ready;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = AppServices.Settings.Settings;

        GamePathBox.Text = settings.GamePath ?? string.Empty;
        GameVersionBox.Text = settings.GameVersionOverride ?? string.Empty;
        ApiBaseUrlBox.Text = settings.ApiBaseUrl;
        ApiKeyBox.Password = settings.ApiKey ?? string.Empty;
        ConfigPathText.Text = $"配置文件：{AppServices.Settings.ConfigFilePath}";

        // 站点名称 / 自定义站点
        SiteNameBox.Text = settings.SiteName ?? string.Empty;
        CustomSiteNameBox.Text = settings.CustomSiteName ?? string.Empty;
        CustomSiteUrlBox.Text = settings.CustomSiteUrl ?? string.Empty;
        CustomSiteKeyBox.Password = settings.CustomSiteApiKey ?? string.Empty;

        RefreshSourceOverview();

        switch ((settings.Theme ?? ThemeService.System).ToLowerInvariant())
        {
            case "light":
                ThemeLight.IsChecked = true;
                break;
            case "dark":
                ThemeDark.IsChecked = true;
                break;
            default:
                ThemeSystem.IsChecked = true;
                break;
        }

        RerunWizardPanel.Visibility = ShowRerunWizard ? Visibility.Visible : Visibility.Collapsed;

        AppVersionText.Text = $"Aurora 版本 v{AppUpdateService.CurrentVersion}";

        CheckOnStartupBox.IsChecked = settings.CheckUpdatesOnStartup;
        CheckGitHubBox.IsChecked = settings.CheckGitHubUpdates;
        DiagnosticLogBox.IsChecked = settings.DiagnosticLogging;

        UpdateGameStatus();
        UpdateGameVersionStatus();

        _ready = true;

        Loaded += (_, _) => PageScrollFix.DisableOuterPageScrolling(this);
    }

    /// <summary>
    /// 「来源」里的网址一律交给系统浏览器打开。
    /// （WPF 的 Hyperlink 在 Page 里默认会让本窗口的 Frame 跳转，把网页渲染进应用里）
    /// </summary>
    private void WebLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) =>
        e.Handled = true;

    private void WebLink_Click(object sender, RoutedEventArgs e)
    {
        var url = sender switch
        {
            System.Windows.Documents.Hyperlink { NavigateUri: { } uri } => uri.AbsoluteUri,
            System.Windows.Documents.Hyperlink { Tag: string tag } => tag,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开就算了
        }
    }

    /// <summary>刷新「来源」总览：默认站点的名字/网址，以及自定义站点那一行。</summary>
    private void RefreshSourceOverview()
    {
        var settings = AppServices.Settings.Settings;

        SourceSiteNameText.Text = AppServices.SiteDisplayName;
        SiteSectionTitle.Text = AppServices.SiteDisplayName;

        SourceSiteHyperlink.Inlines.Clear();
        var siteUrl = settings.ApiBaseUrl;
        if (!string.IsNullOrWhiteSpace(siteUrl))
        {
            SourceSiteHyperlink.NavigateUri = new Uri(siteUrl.TrimEnd('/'));
            SourceSiteHyperlink.Inlines.Add(new System.Windows.Documents.Run(siteUrl.TrimEnd('/')));
        }
        else
        {
            SourceSiteHyperlink.Inlines.Add(new System.Windows.Documents.Run("（未配置）"));
        }

        var customUrl = settings.CustomSiteUrl;
        if (string.IsNullOrWhiteSpace(customUrl))
        {
            CustomSourceRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            CustomSourceRow.Visibility = Visibility.Visible;
            CustomSourceNameText.Text = string.IsNullOrWhiteSpace(settings.CustomSiteName)
                ? "自定义站点"
                : settings.CustomSiteName.Trim();
            CustomSourceUrlText.Text = customUrl.Trim();
        }
    }

    /// <summary>默认站点的显示名（留空 = 按地址自动判断）。</summary>
    private void SiteNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.SiteName = SiteNameBox.Text.Trim();
        AppServices.Settings.Save();
        RefreshSourceOverview();
    }

    /// <summary>自定义站点：名称 / 地址（留空表示不启用）。</summary>
    private void CustomSite_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var settings = AppServices.Settings.Settings;
        settings.CustomSiteName = CustomSiteNameBox.Text.Trim();
        settings.CustomSiteUrl = CustomSiteUrlBox.Text.Trim();
        AppServices.Settings.Save();
        RefreshSourceOverview();
    }

    private void CustomSiteKey_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.CustomSiteApiKey =
            string.IsNullOrWhiteSpace(CustomSiteKeyBox.Password) ? null : CustomSiteKeyBox.Password;
        AppServices.Settings.Save();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        AppServices.Settings.Settings.Theme = tag;
        AppServices.Settings.Save();
        ThemeService.Apply(tag, Window.GetWindow(this));
    }

    // ---------------- 游戏目录 / 版本 ----------------

    private void GamePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.GamePath = GamePathBox.Text.Trim();
        AppServices.Settings.Save();
        UpdateGameStatus();
        UpdateGameVersionStatus();
    }

    private void GameVersionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.GameVersionOverride = GameVersionBox.Text.Trim();
        AppServices.Settings.Save();
        UpdateGameVersionStatus();
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        var best = AppServices.GameLocator.FindBest();
        if (best is null)
        {
            GameStatusText.Text = "未能在 Steam 库中找到《冰与火之舞》，请手动选择游戏目录。";
            return;
        }

        GamePathBox.Text = best;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择《冰与火之舞》游戏目录" };
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
            ? $"✓ 已找到游戏主程序。Mods 目录：{(hasMods ? "存在" : "不存在（可能尚未安装过 mod）")}"
            : "⚠ 该目录里没找到游戏主程序，请确认路径是否正确。";
    }

    private void UpdateGameVersionStatus()
    {
        var path = AppServices.Settings.Settings.GamePath;
        var manual = AppServices.Settings.Settings.GameVersionOverride;

        var detected = string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? null
            : GameVersionReader.TryRead(path!);

        if (!string.IsNullOrWhiteSpace(manual))
        {
            GameVersionStatusText.Text = detected is null
                ? $"✓ 当前使用：{manual}（手动指定）"
                : $"✓ 当前使用：{manual}（手动指定）　·　自动检测为 {detected}";
            return;
        }

        GameVersionStatusText.Text = detected is null
            ? "未能自动识别游戏版本（不影响使用，也可以在上面手动填写）。"
            : $"✓ 已识别游戏版本：{detected}（自动检测）";
    }

    // ---------------- 资源站 ----------------

    private void ApiBaseUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.ApiBaseUrl = ApiBaseUrlBox.Text.Trim();
        AppServices.Settings.Save();
        RefreshSourceOverview();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.ApiKey = ApiKeyBox.Password.Trim();
        AppServices.Settings.Save();
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        var client = AppServices.CreateApiClient();
        if (client is null)
        {
            ApiStatusText.Text = "✗ 请先填写站点地址。";
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
        catch (Exception ex)
        {
            ApiStatusText.Text = "✗ 打开链接失败：" + ex.Message;
        }
    }

    // ---------------- 更新检查 ----------------

    private void CheckOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.CheckUpdatesOnStartup = CheckOnStartupBox.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void CheckGitHub_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AppServices.Settings.Settings.CheckGitHubUpdates = CheckGitHubBox.IsChecked == true;
        AppServices.Settings.Save();
    }

    // ---------------- 诊断与反馈 ----------------

    private void DiagnosticLog_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var enabled = DiagnosticLogBox.IsChecked == true;

        AppServices.Settings.Settings.DiagnosticLogging = enabled;
        AppPaths.DiagnosticLogEnabled = enabled;
        AppServices.Settings.Save();

        DiagnosticsStatusText.Text = enabled
            ? "已开启：日志写在用户目录，反馈问题时可点「导出诊断包」发过来。"
            : "已关闭：只会记录崩溃日志（crash.log）。";
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsStatusText.Text = "打不开日志文件夹：" + ex.Message;
        }
    }

    private void ReportIssue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/lishangi688/adofai-aurora/issues/new")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 导出诊断包到桌面：日志（脱敏）+ 环境说明 + 设置文件（密钥字段打码）。
    /// 用户反馈问题时发这一个 zip 就够了。
    /// </summary>
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, $"Aurora-诊断-{stamp}.zip");

            var utf8 = new System.Text.UTF8Encoding(false);

            using (var zip = System.IO.Compression.ZipFile.Open(
                       path, System.IO.Compression.ZipArchiveMode.Create))
            {
                // ① 日志（读取时再脱敏一次，双保险）
                foreach (var log in Directory.GetFiles(AppPaths.LogDirectory, "*.log"))
                {
                    var entry = zip.CreateEntry("logs/" + Path.GetFileName(log));
                    using var writer = new StreamWriter(entry.Open(), utf8);
                    writer.Write(AppPaths.Redact(File.ReadAllText(log)));
                }

                // ② 环境说明
                var settings = AppServices.Settings.Settings;

                var sys = zip.CreateEntry("system.txt");
                using (var writer = new StreamWriter(sys.Open(), utf8))
                {
                    writer.WriteLine(AppUpdateService.DescribeEnvironment());
                    writer.WriteLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine($"诊断日志：{(settings.DiagnosticLogging ? "开启" : "关闭")}");
                    writer.WriteLine(
                        $"游戏目录：{settings.GamePath ?? "(未设置)"}　存在="
                        + Directory.Exists(settings.GamePath ?? string.Empty));
                    writer.WriteLine($"当前来源：{AppServices.SiteDisplayName}");
                }

                // ③ 设置文件（密钥字段打码）
                var configPath = AppServices.Settings.ConfigFilePath;
                if (File.Exists(configPath))
                {
                    var cfg = zip.CreateEntry("settings.json");
                    using var writer = new StreamWriter(cfg.Open(), utf8);
                    writer.Write(AppPaths.Redact(File.ReadAllText(configPath)));
                }
            }

            DiagnosticsStatusText.Text =
                $"已导出：{path}\n日志里可能含有 24 小时有效的下载直链，建议私聊发送，不要贴到公开的地方。";

            try
            {
                // 用 Shell API 选中导出的诊断包，而不是 explorer.exe /select（后者同样有命令行解析问题）。
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    ShellExplorer.OpenDirectoryAndSelect(dir, Path.GetFileName(path));
                }
            }
            catch
            {
                // 打不开资源管理器也不影响导出
            }
        }
        catch (Exception ex)
        {
            DiagnosticsStatusText.Text = "导出失败：" + ex.Message;
        }
    }

    // ---------------- 关于 ----------------

    private AppUpdateInfo? _pendingUpdate;

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateStatusText.Text = "正在检查…";

        var info = await AppUpdateService.CheckAsync();

        if (info is null)
        {
            _pendingUpdate = null;
            AppUpdateNowButton.Visibility = Visibility.Collapsed;
            AppUpdateStatusText.Text = $"✓ 已是最新版本（v{AppUpdateService.CurrentVersion}）";
            return;
        }

        _pendingUpdate = info;
        AppUpdateNowButton.Visibility = Visibility.Visible;
        AppUpdateStatusText.Text = $"发现新版本 v{info.Version}（来源：{info.SourceLabel}）。";

        // 资源站能给出免鉴权直链时，优先引导走资源站（国内速度稳定）
        var canDownloadFromSite = info.UrlFor(AppUpdateService.Distribution) is not null;

        var text =
            $"发现 Aurora 新版本 v{info.Version}（来源：{info.SourceLabel}）\n"
            + $"当前版本：v{AppUpdateService.CurrentVersion}\n\n"
            + AppUpdateService.DistributionHint + "\n\n";

        if (canDownloadFromSite)
        {
            text += "· 「是」= 立即更新（自动下载；绿色版自动替换并重启，安装版会打开安装程序）\n"
                    + "· 「否」= 打开发布页，自己下载\n"
                    + "· 「取消」= 稍后再更新";
        }
        else
        {
            text += "是否打开下载页？\n"
                    + "（安装版直接运行安装包覆盖安装；绿色版解压替换即可。）";
        }

        var choice = System.Windows.MessageBox.Show(
            text,
            "发现新版本",
            canDownloadFromSite
                ? System.Windows.MessageBoxButton.YesNoCancel
                : System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information);

        if (choice == System.Windows.MessageBoxResult.Yes && canDownloadFromSite)
        {
            AppUpdateNow_Click(sender, e);
            return;
        }

        var target = choice == System.Windows.MessageBoxResult.Yes
            ? info.PageUrl
            : choice == System.Windows.MessageBoxResult.No && canDownloadFromSite
                ? AppUpdateService.ReleasesPageUrl
                : null;

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            AppUpdateStatusText.Text = $"已打开下载页，请下载 v{info.Version}。";
        }
        catch (Exception ex)
        {
            AppUpdateStatusText.Text = "✗ 打开链接失败：" + ex.Message;
        }
    }

    /// <summary>下载新版本并自动替换（绿色版解压覆盖；安装版调用安装程序）。</summary>
    private async void AppUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            AppUpdateStatusText.Text = "请先点「检查更新」。";
            return;
        }

        AppUpdateNowButton.IsEnabled = false;
        AppUpdateProgress.Visibility = Visibility.Visible;
        AppUpdateProgress.IsIndeterminate = true;

        try
        {
            var progress = new InlineProgress<string>(
                text => AppUpdateStatusText.Text = text,
                Dispatcher);

            var result = await AppSelfUpdater.ApplyAsync(
                _pendingUpdate,
                AppUpdateService.Distribution,
                progress);

            AppUpdateProgress.IsIndeterminate = false;
            AppUpdateStatusText.Text = result.Message;

            if (result.ShouldExit)
            {
                AppUpdateProgress.Value = 100;
                await Task.Delay(1500);

                // 退出程序，把"替换文件"交给更新脚本 / 安装程序
                Application.Current.Shutdown();
                return;
            }
        }
        finally
        {
            AppUpdateNowButton.IsEnabled = true;
        }
    }

    // ---------------- 其它 ----------------

    private void RerunWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new Views.Dialogs.FirstRunWizard { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();

        // 向导里可能改了游戏目录 / 资源站，刷新一下界面
        var settings = AppServices.Settings.Settings;
        GamePathBox.Text = settings.GamePath ?? string.Empty;
        ApiBaseUrlBox.Text = settings.ApiBaseUrl;
        ApiKeyBox.Password = settings.ApiKey ?? string.Empty;

        UpdateGameStatus();
        UpdateGameVersionStatus();
    }
}
