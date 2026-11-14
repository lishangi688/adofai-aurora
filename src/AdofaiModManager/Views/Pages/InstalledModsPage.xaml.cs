using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using AdofaiModManager.Views.Dialogs;
using Microsoft.Win32;
using AdofaiModManager.Services.Sources;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Pages;

public partial class InstalledModsPage : Page
{
    private readonly ObservableCollection<InstalledMod> _mods = [];

    private bool _busy;

    public InstalledModsPage()
    {
        InitializeComponent();
        ModsList.ItemsSource = _mods;
        Loaded += async (_, _) =>
        {
            // 更新检查（含启动时的后台检查）完成后，列表同步刷新。
            // 在 Loaded 里订阅（而不是构造函数）：页面被导航缓存后再次显示时也要重新挂上
            AppServices.Updates.Changed -= OnUpdatesChanged;
            AppServices.Updates.Changed += OnUpdatesChanged;

            PageScrollFix.DisableOuterPageScrolling(this);

            // 先把切换动画让出去，再做列表重建：Reload 实测约 100ms，
            // 同步跑在 Loaded 里会把页面切换动画拖住（看起来"顿一下才出来"）。
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var reloadWatch = System.Diagnostics.Stopwatch.StartNew();
                Reload();
                AppPaths.AppendDebugLog($"[perf] 已安装页 Reload {reloadWatch.ElapsedMilliseconds}ms（{_mods.Count} 个）");
            }), System.Windows.Threading.DispatcherPriority.Background);

            // 拉一次资源站映射，用于显示"更新源：资源站"和同步图标；
            // 完成后会触发 Changed → 本页自动再刷新一次
            await AppServices.Updates.EnsureSiteMapAsync();
        };

        Unloaded += (_, _) => AppServices.Updates.Changed -= OnUpdatesChanged;
    }

    private static async Task LoadModIconAsync(InstalledMod mod, string iconUrl)
    {
        var image = await ImageLoader.LoadAsync(iconUrl);
        if (image is not null)
        {
            mod.IconSource = image;
        }
    }

    private void OnUpdatesChanged()
    {
        // 检查完成时会触发一次；Reload 约 100ms，用 Background 优先级延后，
        // 避免正好赶上页面切换/动画时"顿一下"。
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(Reload), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(Reload), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ModService? CreateService()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        return new ModService(gamePath!);
    }

    private void Reload()
    {
        _mods.Clear();

        var service = CreateService();
        if (service is null)
        {
            SummaryText.Text = "尚未设置游戏目录";
            ShowHint("还没有设置游戏目录。\n请到左侧「设置」里自动检测或手动选择《冰与火之舞》的安装目录。");
            return;
        }

        if (!Directory.Exists(service.ModsPath))
        {
            SummaryText.Text = $"没有找到 Mods 文件夹：{service.ModsPath}";
            ShowHint("游戏目录里还没有 Mods 文件夹。\n装过至少一个 mod（或运行过一次游戏）后就会出现。");
            return;
        }

        var updateService = new GitHubUpdateService(AppServices.UpdateSources);
        var gameVersion = AppServices.GameVersion;
        var hasCompatibilityInfo = false;

        var scanned = new List<InstalledMod>();

        foreach (var mod in service.Scan())
        {
            try
            {
                ReloadOne(mod, updateService, gameVersion, ref hasCompatibilityInfo);
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[reload] {mod.Id} 处理失败：{ex}");
            }

            scanned.Add(mod);
        }

        // 可更新的置顶（组内保持原顺序）
        foreach (var mod in scanned.OrderByDescending(m => m.HasUpdate))
        {
            _mods.Add(mod);
        }

        // 检测"重复安装"：同一个 Id 出现在多个文件夹（历史遗留，例如文件夹名与 Id 不一致）。
        // 这种副本 UMM 也会当成同一个 mod，容易导致游戏里加载两份，建议清理掉一份。
        var hasDuplicate = false;

        foreach (var group in scanned.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var folders = group.ToList();
            if (folders.Count < 2)
            {
                continue;
            }

            hasDuplicate = true;

            foreach (var mod in folders)
            {
                var others = string.Join(
                    "、",
                    folders.Where(f => !ReferenceEquals(f, mod))
                           .Select(f => $"「{f.FolderName}」{f.Version}"));

                mod.DuplicateWarning = $"⚠ 重复安装：同一个 mod 出现在多个文件夹（另有 {others}）。建议只保留一份。";
            }
        }

        var enabled = _mods.Count(m => m.IsEnabled);
        var updatable = _mods.Count(m => m.HasUpdate);
        SummaryText.Text = $"共 {_mods.Count} 个 mod（{enabled} 个已启用"
            + (updatable > 0 ? $"，{updatable} 个可更新" : string.Empty)
            + "）"
            + (string.IsNullOrWhiteSpace(gameVersion) ? string.Empty : $"　·　游戏版本 {gameVersion}")
            + (service.IsLoaderInstalled ? string.Empty : "　·　⚠ 未检测到 UMM loader");

        if (_mods.Count == 0)
        {
            ShowHint("Mods 文件夹是空的。\n点右上角「安装本地 zip」安装一个 mod。");
        }
        else
        {
            HideHint();
        }

        if (hasCompatibilityInfo || hasDuplicate)
        {
            var reasons = new List<string>();

            if (hasCompatibilityInfo)
            {
                reasons.Add("有 mod 声明的游戏版本与当前检测不一致");
            }

            if (hasDuplicate)
            {
                reasons.Add("有 mod 重复安装（同一个 mod 出现在多个文件夹）");
            }

            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = hasDuplicate ? "需要处理的提醒" : "版本兼容提醒";
            StatusBar.Message = string.Join("；", reasons) + "。详情见各条目里的提示。";
            StatusBar.IsOpen = true;
        }

        // 扫描时自动恢复了上次没完成的更新（也可能发生在启动时的后台检查里）：必须让用户知道
        var recovered = ModService.TakeRecoveryNotes();
        if (recovered.Count > 0)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "已恢复中断的更新";
            StatusBar.Message = string.Join("\n", recovered);
            StatusBar.IsOpen = true;
        }

        if (AppPaths.DebugLogEnabled)
        {
            Dispatcher.BeginInvoke(
                new Action(() => AppPaths.AppendDebugLog(
                    $"[installed] Extent={ModsScroller.ExtentHeight} Viewport={ModsScroller.ViewportHeight} Scrollable={ModsScroller.ScrollableHeight}")),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 处理单个已安装 mod 的显示字段（更新源 / 更新状态 / 图标 / 兼容性提示）。
    /// 单独抽出来是为了让调用方能逐个 try/catch —— 任何一个 mod 出问题都不该影响其它卡片。
    /// </summary>
    private void ReloadOne(
        InstalledMod mod,
        GitHubUpdateService updateService,
        string? gameVersion,
        ref bool hasCompatibilityInfo)
    {
        var source = updateService.ResolveSource(mod);
        var site = AppServices.Updates.FindSiteMod(mod);

        if (source.Kind != UpdateSourceKind.None)
        {
            mod.UpdateSourceLabel = $"更新源：{source.Description}{(source.IsManual ? "（手动）" : string.Empty)}";
        }
        else if (site is not null)
        {
            mod.UpdateSourceLabel = $"更新源：{AppServices.SiteDisplayName}";
        }
        else
        {
            mod.UpdateSourceLabel = "更新源：未设置（可点「绑定 GitHub」手动指定）";
        }

        // 图标尽量与资源站保持一致（走本地图标缓存，不会每次重新下载）
        if (mod.IconSource is null && !string.IsNullOrWhiteSpace(site?.IconUrl))
        {
            _ = LoadModIconAsync(mod, site!.IconUrl!);
        }

        if (AppServices.Updates.Get(mod) is { } result)
        {
            var via = string.IsNullOrWhiteSpace(result.SourceLabel) ? string.Empty : $"（{result.SourceLabel}）";

            mod.UpdateStatus = result.Success
                ? result.UpdateAvailable
                    ? $"可更新 → {result.RemoteVersion}{via}"
                    : string.IsNullOrWhiteSpace(result.RemoteVersion)
                        ? "无可用更新源"
                        : result.VersionSchemeMismatch
                            ? $"版本号规则不同（{result.SourceLabel ?? AppServices.SiteDisplayName} {result.RemoteVersion} / 本地 {mod.Version}）"
                            : $"已是最新{via}"
                : $"检查失败{via}";

            // 次要来源的情况（例如"资源站 2.5.0"或"GitHub 未连通"）
            if (!string.IsNullOrWhiteSpace(result.SecondaryNote))
            {
                mod.UpdateStatus += $"　·　{result.SecondaryNote}";
            }

            mod.HasUpdate = result.Success && result.UpdateAvailable;
            mod.RemoteVersion = result.RemoteVersion;
            mod.UpdateDownloadUrl = result.DownloadUrl;
            mod.UpdateFileName = result.FileName;
        }

        if (!string.IsNullOrWhiteSpace(mod.GameVersion) &&
            !string.IsNullOrWhiteSpace(gameVersion) &&
            !VersionsMatch(mod.GameVersion!, gameVersion!))
        {
            mod.CompatibilityWarning = $"⚠ 该 mod 要求游戏 {mod.GameVersion}，当前检测为 {gameVersion}";
            hasCompatibilityInfo = true;
        }
    }

    private static bool VersionsMatch(string a, string b) =>
        KernelService.CompareVersions(a, b) == 0;

    private void ShowHint(string text)
    {
        EmptyHint.Text = text;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void HideHint() => EmptyHint.Visibility = Visibility.Collapsed;

    private void Report(bool success, string message)
    {
        StatusBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = success ? "提示" : "出错了";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private async void InstallZip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            Report(false, "另一个操作正在进行，请完成后再试。");
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 UMM 格式的 mod 压缩包",
            Filter = "Mod 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 安装可能要处理很大的旧 Mod 目录（硬链接通常很快，但不要阻塞 UI 线程）
        _busy = true;
        var button = sender as FrameworkElement;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "正在安装";
        StatusBar.Message = $"{Path.GetFileName(dialog.FileName)}";
        StatusBar.IsOpen = true;

        try
        {
            var result = await Task.Run(() => service.InstallFromZip(dialog.FileName));
            Report(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[install] 本地 zip 安装失败：{ex}");
            Report(false, $"安装失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            if (button is not null)
            {
                button.IsEnabled = true;
            }

            Reload();
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"确定要卸载「{mod.DisplayName}」吗？\n这会删除文件夹：{mod.FolderPath}",
            "确认卸载",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var result = service.Uninstall(mod);
        Report(result.Success, result.Message);
        AppServices.Updates.Clear(mod);
        Reload();
    }

    private void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not InstalledMod mod)
        {
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            return;
        }

        var result = service.SetEnabled(mod, checkBox.IsChecked == true);
        Report(result.Success, result.Message);
        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        // 更新时会先把旧目录改名成 .aurora-old-*，这时原路径暂时不存在；
        // 而 Windows Explorer 遇到不存在的路径不会报错，会直接打开「文档」文件夹，
        // 看起来就像 Aurora 打开错了地方。这里先自己判断，并给出明确提示。
        if (_busy)
        {
            Report(false, "正在更新或检查，请完成后再打开文件夹。");
            return;
        }

        if (!Directory.Exists(mod.FolderPath))
        {
            AppPaths.AppendDebugLog($"[open] mod={mod.Id} path={mod.FolderPath} exists=false");
            Report(false, "这个 Mod 的文件夹不存在，可能已被移动或删除。已刷新列表，请重试。");
            Reload();
            return;
        }

        try
        {
            PathToExplorer.Open(mod.FolderPath);
        }
        catch (InvalidPathException ex)
        {
            AppPaths.AppendDebugLog($"[open] 无效路径: {mod.FolderPath}");
            Report(false, $"这个 Mod 的文件夹路径包含无效字符，无法打开（{ex.Message}）。");
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[open] mod={mod.Id} path={mod.FolderPath} 失败：{ex.Message}");
            Report(false, $"打开文件夹失败：{ex.Message}");
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        _busy = true;
        try
        {
            // 单条检查也走"资源站 + GitHub 双来源"，与整体检查保持一致
            var result = await AppServices.Updates.CheckOneAsync(mod, forceRefresh: true);
            Report(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            Report(false, $"检查更新失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        var mods = service.Scan();

        _busy = true;
        CheckAllButton.IsEnabled = false;

        try
        {
            var progress = new InlineProgress<string>(name =>
            {
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.Title = "正在检查更新";
                StatusBar.Message = name;
                StatusBar.IsOpen = true;
            }, Dispatcher);

            await AppServices.Updates.CheckAllAsync(mods, progress, forceRefresh: true);

            var updatable = mods.Count(m => AppServices.Updates.Get(m) is { Success: true, UpdateAvailable: true });
            Report(true, $"检查完成：{mods.Count} 个 mod，{updatable} 个可更新。");
        }
        catch (Exception ex)
        {
            Report(false, $"检查更新失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            CheckAllButton.IsEnabled = true;
            Reload();
        }
    }

    private static AdofaiToolsClient? BuildSiteClient()
    {
        return AppServices.CreateApiClient();
    }

    /// <summary>按资源站信息安装（返回 null 表示没有配置站点 / 没有 slug）。expectedId：被更新 mod 的 Id。</summary>
    private static async Task<InstallResult?> InstallFromSiteAsync(
        ModService service,
        UpdateCheckResult result,
        IProgress<int>? progress,
        string expectedId)
    {
        var client = BuildSiteClient();
        if (client is null || string.IsNullOrWhiteSpace(result.SiteSlug))
        {
            return null;
        }

        var detail = await client.GetModDetailAsync(result.SiteResourceType ?? "MOD", result.SiteSlug!);
        return await new SiteInstaller(client, service).InstallAsync(detail, progress, expectedId: expectedId);
    }

    /// <summary>按第三方源（TUF / modlist）的信息安装更新（返回 null 表示解析不到）。expectedId：被更新 mod 的 Id。</summary>
    private static async Task<InstallResult?> InstallFromRemoteAsync(
        ModService service,
        UpdateCheckResult result,
        IProgress<int>? progress,
        string expectedId)
    {
        var source = RemoteSources.ById(result.RemoteSourceId);
        if (source is null || string.IsNullOrWhiteSpace(result.RemoteSlug))
        {
            return null;
        }

        var detail = await source.GetModDetailAsync(result.RemoteSlug!);
        if (detail is null)
        {
            return null;
        }

        var version = detail.Versions.FirstOrDefault(v =>
                          string.Equals(v.VersionId, result.RemoteVersion, StringComparison.OrdinalIgnoreCase))
                      ?? detail.Versions.FirstOrDefault();

        if (version is null)
        {
            return null;
        }

        return await new RemoteInstaller(source, service).InstallAsync(detail.Mod, version, progress, expectedId: expectedId);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        // 正在更新 / 检查时不接受新的点击：连点两下会让两个安装同时改同一个文件夹
        if (_busy || (sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        var result = AppServices.Updates.Get(mod);
        if (result is null || !result.UpdateAvailable)
        {
            Report(false, "请先「检查更新」。");
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        _busy = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "正在更新";
        StatusBar.Message = $"{mod.DisplayName} → {result.RemoteVersion}";
        StatusBar.IsOpen = true;

        try
        {
            var progress = new InlineProgress<int>(percent =>
                StatusBar.Message = $"{mod.DisplayName} → {result.RemoteVersion}　下载中 {percent}%", Dispatcher);

            InstallResult install;
            var usedSiteFallback = false;

            if (!string.IsNullOrWhiteSpace(result.RemoteSourceId))
            {
                // 更新来自第三方源（TUF / modlist）：解析下载地址 → 校验 → 安装
                var fromRemote = await InstallFromRemoteAsync(service, result, progress, mod.Id);
                if (fromRemote is null)
                {
                    Report(false, $"该更新来自 {result.SourceLabel}，但没能解析出可用的下载地址。");
                    return;
                }

                install = fromRemote;
            }
            else if (!string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                var cacheName = string.IsNullOrWhiteSpace(result.RemoteVersion)
                    ? null
                    : $"{mod.Id}-{result.RemoteVersion}";

                install = await service.InstallFromUrlAsync(result.DownloadUrl, progress, default, cacheName, mod.Id);

                // GitHub 下载失败（代理挂了 / 被墙）→ 自动改用资源站那一版，别让用户卡住
                if (!install.Success && result.HasSiteFallback)
                {
                    var fallback = await InstallFromSiteAsync(service, result, progress, mod.Id);
                    if (fallback is not null)
                    {
                        install = fallback;
                        usedSiteFallback = true;
                    }
                }
            }
            else if (result.HasSiteFallback)
            {
                var fromSite = await InstallFromSiteAsync(service, result, progress, mod.Id);
                if (fromSite is null)
                {
                    Report(false, $"该更新来自 {AppServices.SiteDisplayName}，但未配置 API key。");
                    return;
                }

                install = fromSite;
            }
            else
            {
                Report(false, "该更新没有可用的下载来源。");
                return;
            }

            if (usedSiteFallback)
            {
                Report(
                    install.Success,
                    install.Success
                        ? $"{install.Message}\n（GitHub 下载失败，已改用 {AppServices.SiteDisplayName} 版本 {result.RemoteVersion}）"
                        : install.Message);
            }
            else
            {
                Report(install.Success, install.Message);
            }

            AppServices.Updates.Clear(mod);
        }
        catch (AdofaiToolsException ex)
        {
            Report(false, ex.Message);
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[update] {mod.Id} 更新失败：{ex}");
            Report(false, $"更新失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private void BindGitHub_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        var current = AppServices.UpdateSources.GetOverride(mod.Id)
                      ?? mod.Repository
                      ?? mod.HomePage
                      ?? string.Empty;

        var dialog = new TextInputDialog(
            "绑定 GitHub 更新源",
            $"为「{mod.DisplayName}」指定 GitHub 仓库地址，或 Repository.json 的直链。\n"
            + "例如：https://github.com/作者/仓库\n"
            + "留空并确定可清除手动设置，恢复自动识别。",
            current)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AppServices.UpdateSources.SetOverride(mod.Id, dialog.InputText);
        AppServices.Updates.Clear(mod);
        Reload();
    }
}
