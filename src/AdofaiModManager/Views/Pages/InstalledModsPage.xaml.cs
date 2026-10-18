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
    private readonly ObservableCollection<InstalledModGroup> _groups = [];

    private bool _busy;

    private CancellationTokenSource? _reloadDebounce;

    private bool _reloading;

    private bool _reloadRequested;

    public InstalledModsPage()
    {
        InitializeComponent();
        ModsList.ItemsSource = _groups;
        Loaded += async (_, _) =>
        {
            PageScrollFix.DisableOuterPageScrolling(this);
            Reload();

            // 拉一次资源站映射，用于显示"更新源：资源站"和同步图标；
            // 完成后会触发 Changed → 本页自动再刷新一次
            await AppServices.Updates.EnsureSiteMapAsync();
        };

        // 更新检查（含启动时的后台检查）完成后，列表同步刷新
        AppServices.Updates.Changed += OnUpdatesChanged;
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ScheduleReload);
            return;
        }

        ScheduleReload();
    }

    /// <summary>
    /// 更新检查会连续触发好几次 Changed（每个来源改一次状态都会发），
    /// 每次都整页重建会明显卡顿，所以合并成一次、并且延后一点点再刷。
    /// </summary>
    private void ScheduleReload()
    {
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = new CancellationTokenSource();

        _ = ReloadAfterDelayAsync(_reloadDebounce.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(120, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Reload();
    }

    private static ModService? CreateService()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        return ModService.Get(gamePath!);
    }

    private void Reload() => _ = ReloadAsync();

    /// <summary>
    /// 整页刷新。扫描游戏目录要读几百个文件、还要解析程序集版本，放在后台线程做，
    /// 否则切页面 / 更新状态变化时界面会先卡住一下。
    /// 同时串行化：刷新期间又来了新的刷新请求，只记下来，等这一轮结束后再补一次。
    /// </summary>
    private async Task ReloadAsync()
    {
        if (_reloading)
        {
            _reloadRequested = true;
            return;
        }

        _reloading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                await ReloadCoreAsync();
            }
            while (_reloadRequested);
        }
        finally
        {
            _reloading = false;
        }
    }

    private async Task ReloadCoreAsync()
    {
        _groups.Clear();

        var service = CreateService();
        if (service is null)
        {
            SummaryText.Text = "尚未设置游戏目录";
            ShowHint("还没有设置游戏目录。\n请到左侧「设置」里自动检测或手动选择《冰与火之舞》的安装目录。");
            return;
        }

        var updateService = new GitHubUpdateService(AppServices.UpdateSources);
        var hasCompatibilityInfo = false;

        var scanned = new List<InstalledMod>();

        // 扫描要遍历 Mods / UMMMods / Plugins / BepInEx\plugins 并解析程序集版本，
        // 读游戏版本也要读磁盘 —— 全部放后台线程，别让界面先卡住。
        var (installed, gameVersion, ummVersion) = await Task.Run(() =>
        {
            try
            {
                return (service.Scan(), AppServices.GameVersion, service.Environment.UmmVersion);
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[reload] 扫描 mod 失败：{ex}");
                return (Array.Empty<InstalledMod>() as IReadOnlyList<InstalledMod>, null, null);
            }
        });

        foreach (var mod in installed)
        {
            try
            {
                ReloadOne(mod, updateService, gameVersion, ummVersion, ref hasCompatibilityInfo);
            }
            catch (Exception ex)
            {
                AppPaths.AppendDebugLog($"[reload] {mod.Id} 处理失败：{ex}");
            }

            scanned.Add(mod);
        }

        // 检测"重复安装"：同一个加载器下，同一个 Id 出现在多个文件夹。
        // 不同加载器里的同名 mod 不算重复（例如同一个 mod 的 UMM / MelonLoader 两个包）。
        var hasDuplicate = false;
        var hasCrossLoader = false;

        foreach (var group in scanned.GroupBy(
                     m => $"{(int)m.Loader}\u0001{m.Id}",
                     StringComparer.OrdinalIgnoreCase))
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

                mod.DuplicateWarning = $"⚠ 重复安装：同一个加载器下出现了多份（另有 {others}）。建议只保留一份。";
            }
        }

        // 同一个 Mod 同时装了 UMM 和 MelonLoader 两个版本时，UMMBridge 会把它加载两次。
        foreach (var sameId in scanned
                     .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Select(m => m.Loader).Distinct().Count() > 1))
        {
            var loaders = sameId.Select(m => m.Loader).Distinct().ToList();
            if (!loaders.Contains(ModLoaderKind.UnityModManager) ||
                !loaders.Contains(ModLoaderKind.MelonLoader))
            {
                continue;
            }

            hasCrossLoader = true;
            foreach (var mod in sameId)
            {
                mod.CrossLoaderWarning =
                    "⚠ 同一个 Mod 同时安装在 UnityModManager 和 MelonLoader 中；使用 UMMBridge 时可能被加载两次并导致游戏崩溃，建议只保留一个版本。";
            }
        }

        // 按加载器分组；组内把可更新的放前面。
        foreach (var loaderGroup in scanned
                     .GroupBy(m => m.Loader)
                     .OrderBy(g => g.Key switch
                     {
                         ModLoaderKind.UnityModManager => 0,
                         ModLoaderKind.MelonLoader => 1,
                         ModLoaderKind.BepInEx => 2,
                         _ => 3,
                     }))
        {
            var mods = new ObservableCollection<InstalledMod>(
                loaderGroup.OrderByDescending(m => m.HasUpdate));

            if (mods.Count > 0)
            {
                _groups.Add(new InstalledModGroup { Loader = loaderGroup.Key, Mods = mods });
            }
        }

        var total = scanned.Count;
        var enabled = scanned.Count(m => m.IsEnabled);
        var updatable = scanned.Count(m => m.HasUpdate);

        SummaryText.Text = $"{total} 个 Mod　·　{enabled} 个已启用"
            + (updatable > 0 ? $"　·　{updatable} 个可更新" : string.Empty)
            + (string.IsNullOrWhiteSpace(gameVersion) ? string.Empty : $"　·　游戏 {gameVersion}")
            + (service.Environment.HasInjection ? string.Empty : "　·　⚠ 未检测到有效加载器注入");

        if (total == 0)
        {
            ShowHint(
                "没有找到任何加载器下的 mod。\n"
                + $"UMM：{service.ModsPath}\n"
                + $"MelonLoader：{service.MelonModsPath}\n"
                + $"BepInEx：{service.BepInExPluginsPath}\n\n"
                + "点右上角「安装本地 zip」安装一个 mod。");
        }
        else
        {
            HideHint();
        }

        if (hasCompatibilityInfo || hasDuplicate || hasCrossLoader)
        {
            var reasons = new List<string>();

            if (hasCompatibilityInfo)
            {
                reasons.Add("有 mod 声明的游戏版本与当前检测不一致");
            }

            if (hasDuplicate)
            {
                reasons.Add("有 mod 重复安装（同一个加载器下出现多份）");
            }

            if (hasCrossLoader)
            {
                reasons.Add("有 mod 同时装在 UMM 和 MelonLoader 中，UMMBridge 下可能冲突");
            }

            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = hasDuplicate || hasCrossLoader ? "需要处理的提醒" : "版本兼容提醒";
            StatusBar.Message = string.Join("；", reasons) + "。详情见各条目里的提示。";
            StatusBar.IsOpen = true;
        }
        else
        {
            StatusBar.IsOpen = false;
        }

        if (AppPaths.DebugLogEnabled)
        {
            _ = Dispatcher.BeginInvoke(
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
        string? ummVersion,
        ref bool hasCompatibilityInfo)
    {
        var source = updateService.ResolveSource(mod);
        var site = AppServices.Updates.FindSiteMod(mod);

        if (source.Kind != UpdateSourceKind.None)
        {
            mod.UpdateSourceLabel = $"更新源：{FriendlySource(source)}{(source.IsManual ? "（手动）" : string.Empty)}";
        }
        else if (site is not null)
        {
            mod.UpdateSourceLabel = $"更新源：{AppServices.SiteDisplayName}";
        }
        else
        {
            mod.UpdateSourceLabel = string.Empty;
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
                            ? $"版本号规则不同（ADOFAITools {result.RemoteVersion} / 本地 {mod.Version}）"
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

        // UMM 的 Info.json.GameVersion 语义是“最低游戏版本”，不是精确版本。
        // 只有当前游戏版本低于它时才提示；游戏更新到更高版本不应误报。
        if (!string.IsNullOrWhiteSpace(mod.GameVersion) &&
            !string.IsNullOrWhiteSpace(gameVersion) &&
            KernelService.CompareVersions(mod.GameVersion, gameVersion) > 0)
        {
            mod.CompatibilityWarning = $"⚠ 该 mod 要求游戏至少 {mod.GameVersion}，当前检测为 {gameVersion}";
            hasCompatibilityInfo = true;
        }

        // UMM 的 ManagerVersion 同样是“最低 UMM 版本”。
        if (mod.Loader == ModLoaderKind.UnityModManager &&
            !string.IsNullOrWhiteSpace(mod.ManagerVersion) &&
            !string.IsNullOrWhiteSpace(ummVersion) &&
            KernelService.CompareVersions(mod.ManagerVersion, ummVersion) > 0)
        {
            mod.CompatibilityWarning = string.IsNullOrWhiteSpace(mod.CompatibilityWarning)
                ? $"⚠ 该 mod 要求 UMM 至少 {mod.ManagerVersion}，当前为 {ummVersion}"
                : mod.CompatibilityWarning + $"\n⚠ 该 mod 要求 UMM 至少 {mod.ManagerVersion}，当前为 {ummVersion}";
            hasCompatibilityInfo = true;
        }
    }

    private static string FriendlySource(UpdateSource source)
    {
        if (source.Kind == UpdateSourceKind.GitHubRepo)
        {
            return "GitHub";
        }

        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            if (source.Url.Contains("github", StringComparison.OrdinalIgnoreCase))
            {
                return "GitHub";
            }

            if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
        }

        return "自定义源";
    }

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

    private void InstallZip_Click(object sender, RoutedEventArgs e)
    {
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

        var result = service.InstallFromZip(dialog.FileName);
        Report(result.Success, result.Message);
        Reload();
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

    private void ToggleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not InstalledMod mod)
        {
            return;
        }

        var service = CreateService();
        if (service is null)
        {
            return;
        }

        var result = service.SetEnabled(mod, toggle.IsChecked == true);
        Report(result.Success, result.Message);
        Reload();
    }

    /// <summary>卡片右侧的「⋯」：把低频操作收进菜单，保持列表简洁。</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not InstalledMod mod)
        {
            return;
        }

        var menu = new ContextMenu();

        System.Windows.Controls.MenuItem Item(string header, RoutedEventHandler handler)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header, DataContext = mod };
            item.Click += handler;
            return item;
        }

        menu.Items.Add(Item("检查更新", CheckUpdate_Click));
        menu.Items.Add(Item("绑定 GitHub", BindGitHub_Click));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开文件夹", OpenFolder_Click));
        menu.Items.Add(Item("卸载", Uninstall_Click));

        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        try
        {
            // MelonLoader / BepInEx 的单个 DLL mod，FolderPath 是文件：
            // ShellExplorer 会打开它所在目录并选中该文件。
            // 注意不能用 explorer.exe "路径"——带引号的命令行参数经常被忽略，会退化成打开「文档」。
            ShellExplorer.OpenFolder(mod.FolderPath);
        }
        catch (Exception ex)
        {
            Report(false, $"打开文件夹失败：{ex.Message}");
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        // 单条检查也走"资源站 + GitHub 双来源"，与整体检查保持一致
        var result = await AppServices.Updates.CheckOneAsync(mod, forceRefresh: true);
        Report(result.Success, result.Message);
        Reload();
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

    /// <summary>按资源站信息安装（返回 null 表示没有配置站点 / 没有 slug）。</summary>
    private static async Task<InstallResult?> InstallFromSiteAsync(
        ModService service,
        UpdateCheckResult result,
        IProgress<int>? progress,
        InstalledMod? target = null)
    {
        var client = BuildSiteClient();
        if (client is null || string.IsNullOrWhiteSpace(result.SiteSlug))
        {
            return null;
        }

        var detail = await client.GetModDetailAsync(result.SiteResourceType ?? "MOD", result.SiteSlug!);
        return await new SiteInstaller(client, service).InstallAsync(detail, progress, target: target);
    }

    /// <summary>按第三方源（TUF / modlist）的信息安装更新（返回 null 表示解析不到）。</summary>
    private static async Task<InstallResult?> InstallFromRemoteAsync(
        ModService service,
        UpdateCheckResult result,
        IProgress<int>? progress,
        InstalledMod target)
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

        return await new RemoteInstaller(source, service).InstallAsync(detail.Mod, version, progress, target: target);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledMod mod)
        {
            return;
        }

        // MelonLoader / BepInEx 的 mod 也能更新：本质上都是"下载 zip 再装回去"，
        // 装的时候带上 mod.Loader 和它原来的位置，保证还是装回同一个地方。
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
                var fromRemote = await InstallFromRemoteAsync(service, result, progress, mod);
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

                // 带上 mod 本身：更新时按它原来的加载器与目录装回去。
                install = await service.InstallFromUrlAsync(result.DownloadUrl, progress, default, cacheName, mod);

                // GitHub 下载失败（代理挂了 / 被墙）→ 自动改用资源站那一版，别让用户卡住
                if (!install.Success && result.HasSiteFallback)
                {
                    var fallback = await InstallFromSiteAsync(service, result, progress, mod);
                    if (fallback is not null)
                    {
                        install = fallback;
                        usedSiteFallback = true;
                    }
                }
            }
            else if (result.HasSiteFallback)
            {
                var fromSite = await InstallFromSiteAsync(service, result, progress, mod);
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
