using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using AdofaiModManager.Services.Sources;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Pages;

public partial class OnlineModsPage : Page
{
    private readonly RangeObservableCollection<ModListItem> _mods = [];

    private readonly HashSet<string> _installedIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _installedVersions = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _ready;

    private bool _busy;

    private int _page = 1;

    private int _total;

    /// <summary>是否已经把所有页都加载完了（用于滚动加载）</summary>
    private bool _allLoaded;

    private ScrollViewer? _listScroller;

    private bool _scrollHooked;

    private readonly int _pageSize = 30;

    private ModDetail? _detail;

    /// <summary>当前选中的第三方来源；为 null 表示走原有资源站通道。</summary>
    private IRemoteSource? _remoteSource;

    /// <summary>第三方来源的原始详情（安装时要用它解析下载地址）。</summary>
    private RemoteModDetail? _remoteDetail;

    /// <summary>已安装 mod 按"规范化名字"索引，给第三方来源比对本机状态用。</summary>
    private readonly Dictionary<string, string> _installedByNormalized = new(StringComparer.OrdinalIgnoreCase);

    public OnlineModsPage()
    {
        InitializeComponent();

        ModList.ItemsSource = _mods;
        SortCombo.SelectedIndex = 0;

        // 来源选择：第一项是原有资源站通道（行为不变），后面是第三方源
        SourceCombo.ItemsSource = RemoteSources.Choices;
        SourceCombo.SelectedIndex = 0;
        SubtitleText.Text = "浏览各来源的 mod，一键下载安装。";

        _ready = true;

        // 首屏也要显示署名：初始化时 SelectedIndex 的变化被 _ready 挡掉了，
        // 这里补一次（否则默认的 ADOFAI Tools 会没有署名行）。
        UpdateSourceCredit();

        if (AppPaths.DebugLogEnabled)
        {
            ModList.PreviewMouseLeftButtonDown += (_, _) => DebugLog("ListBox 收到 PreviewMouseLeftButtonDown");
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                var p = e.GetPosition(this);
                var sp = PointToScreen(p);
                DebugLog($"Page 点击：页面逻辑=({p.X:0},{p.Y:0}) 屏幕=({sp.X:0},{sp.Y:0}) 源={e.OriginalSource?.GetType().Name}");
            };

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        var tl = ModList.PointToScreen(new Point(0, 0));
                        DebugLog($"ListBox 屏幕范围 左上=({tl.X:0},{tl.Y:0}) 尺寸={ModList.ActualWidth:0}x{ModList.ActualHeight:0}");
                    }
                    catch
                    {
                        // 忽略
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        Loaded += (_, _) =>
        {
            PageScrollFix.DisableOuterPageScrolling(this);
            HookListScroll();

            if (_mods.Count == 0)
            {
                _ = SearchAsync(1);
            }
        };
    }

    /// <summary>
    /// 给 mod 列表内部的滚动条挂上事件，滚到接近底部时自动加载下一页
    /// （取代原来的「上一页 / 下一页」分页按钮）。
    /// </summary>
    private void HookListScroll()
    {
        if (_scrollHooked)
        {
            return;
        }

        _listScroller ??= FindScrollViewer(ModList);
        if (_listScroller is null)
        {
            return;
        }

        _listScroller.ScrollChanged += OnListScrollChanged;
        _scrollHooked = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_busy || _allLoaded || e.ExtentHeight <= 0)
        {
            return;
        }

        // 距离底部还剩不到一屏时，提前把下一页拉进来
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300)
        {
            _ = LoadMoreAsync();
        }
    }

    /// <summary>
    /// 记录当前列表是基于哪一组筛选条件加载的。
    /// 作用：筛选条件改了但还没重新搜索时，禁止"继续加载下一页"——
    /// 否则会拿新筛选词 + 旧页码去请求，出现"共 1 个资源却已加载 30 个"这种自相矛盾的状态。
    /// </summary>
    private string _loadedFilter = string.Empty;

    /// <summary>远程列表的"请求代号"：切来源/重新搜索时自增，用来丢掉过期结果。</summary>
    private int _remoteRequestId;

    /// <summary>当前这次远程加载的取消令牌（切来源时把上一次取消掉）。</summary>
    private CancellationTokenSource? _remoteCts;

    /// <summary>资源站"后台补页"的代号：重新搜索/切来源时自增，旧的补页循环会自己退出。</summary>
    private int _siteFillId;

    /// <summary>资源站主请求的代号：切来源/重新搜索时自增，旧请求结果直接丢弃。</summary>
    private int _siteRequestId;

    private string CurrentFilterKey()
    {
        var sort = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var type = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        var source = _remoteSource?.Id ?? "site";
        return $"{source}|{SearchBox.Text.Trim()}|{sort}|{type}";
    }

    /// <summary>加载下一页并追加到列表末尾（筛选条件必须和已加载的一致）。</summary>
    private Task LoadMoreAsync()
    {
        if (CurrentFilterKey() != _loadedFilter)
        {
            return Task.CompletedTask;
        }

        return FetchAsync(_page + 1, append: true);
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

    private static ModService? BuildModService()
    {
        var gamePath = AppServices.Settings.Settings.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return null;
        }

        return new ModService(gamePath);
    }

    private static readonly TimeSpan InstalledCacheTtl = TimeSpan.FromSeconds(5);
    private DateTime _installedCacheAt = DateTime.MinValue;

    private void LoadInstalled(bool force = false)
    {
        if (!force && DateTime.UtcNow - _installedCacheAt < InstalledCacheTtl)
        {
            return;
        }

        _installedIds.Clear();
        _installedVersions.Clear();
        _installedByNormalized.Clear();

        var service = BuildModService();
        if (service is null)
        {
            _installedCacheAt = DateTime.UtcNow;
            return;
        }

        foreach (var mod in service.Scan())
        {
            _installedIds.Add(mod.Id);
            if (!string.IsNullOrWhiteSpace(mod.Version))
            {
                _installedVersions[mod.Id] = mod.Version!;
            }

            // 第三方来源没有资源站那种 Id 映射，只能按显示名 / 规范化名字比对
            if (!string.IsNullOrWhiteSpace(mod.DisplayName))
            {
                _installedIds.Add(mod.DisplayName);
                if (!string.IsNullOrWhiteSpace(mod.Version))
                {
                    _installedVersions[mod.DisplayName] = mod.Version!;
                }

                var normalized = UpdateCenter.NormalizeName(mod.DisplayName);
                if (normalized.Length > 0)
                {
                    _installedByNormalized[normalized] = mod.DisplayName;
                }
            }
        }

        _installedCacheAt = DateTime.UtcNow;
    }

    private void ApplyLocalState(ModListItem item)
    {
        var ummId = AppServices.InstallMap.GetUmmId(item.Id);

        string? matched = null;
        if (ummId is not null && _installedIds.Contains(ummId))
        {
            matched = ummId;
        }
        else if (_installedIds.Contains(item.DisplayName))
        {
            matched = item.DisplayName;
        }
        else if (_installedByNormalized.TryGetValue(UpdateCenter.NormalizeName(item.DisplayName), out var normalizedMatch))
        {
            matched = normalizedMatch;
        }

        if (matched is null)
        {
            item.LocalState = null;
            return;
        }

        var local = _installedVersions.TryGetValue(matched, out var version) ? version : null;
        var remote = item.LatestVersion?.VersionId;

        // 版本号写法不同时（例：本地 26w40c / 资源站 26.5.1）不做逐段比较，
        // 只比第一个数字段：能确定更新才标"可更新"，避免误报。
        item.LocalState = VersionScheme.IsRemoteNewer(local, remote) ? "可更新" : "已安装";
    }

    private Task SearchAsync(int page) => FetchAsync(page, append: false);

    /// <param name="page">要请求的页码</param>
    /// <param name="append">true = 追加到列表末尾（滚动加载）；false = 重新搜索（清空重来）</param>
    private async Task FetchAsync(int page, bool append)
    {
        if (append && (_busy || _allLoaded))
        {
            return;
        }

        // 切来源 / 重新搜索时，允许直接取代正在进行的请求，而不是等旧请求超时。
        if (!append)
        {
            _busy = false;
            _remoteCts?.Cancel();
            _siteFillId++;
            _remoteRequestId++;
        }

        if (_remoteSource is { } remote)
        {
            await FetchRemoteAsync(remote, page, append);
            return;
        }

        var siteRequestId = ++_siteRequestId;

        var client = BuildClient(out var error);
        if (client is null)
        {
            Report(false, error);
            SubtitleText.Text = error;

            if (!append)
            {
                _mods.Clear();
                UpdateFooter();
            }

            return;
        }

        SetBusy(true);

        if (!append)
        {
            SubtitleText.Text = $"正在从 {AppServices.SiteDisplayName} 加载…";
        }

        try
        {
            if (!append)
            {
                LoadInstalled();
            }

            // 这次请求用的筛选条件（请求过程中用户可能又改了输入框，所以先固定下来）
            var filterKey = CurrentFilterKey();

            var sort = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var type = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var search = SearchBox.Text;

            var result = await client.GetModsAsync(page, _pageSize, search, type, null, sort);

            if (siteRequestId != _siteRequestId)
            {
                return;
            }

            ApplySitePage(result, append, filterKey);

            SubtitleText.Text = _mods.Count == 0
                ? "没有找到匹配的 mod。"
                : $"{AppServices.SiteDisplayName} 共 {result.Total} 个资源，已加载 {_mods.Count} 个。";

            // 首屏出来以后，后台把剩下的页也补齐（往下滚就不会"划到底就没了"）
            if (!append && !_allLoaded)
            {
                var fillId = ++_siteFillId;
                _ = FillRemainingSitePagesAsync(client, filterKey, search, type, sort, fillId);
            }
        }
        catch (AdofaiToolsException ex)
        {
            if (siteRequestId != _siteRequestId)
            {
                return;
            }

            Report(false, ex.Message);
            SubtitleText.Text = ex.Message;
        }
        finally
        {
            if (siteRequestId == _siteRequestId)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>把资源站的一页结果铺到列表上（首次加载 / 滚动加载 / 后台补页共用）。</summary>
    private void ApplySitePage(ModListPage result, bool append, string filterKey)
    {
        if (!append)
        {
            _page = 1;
            _allLoaded = false;
        }

        _loadedFilter = filterKey;
        _page = result.Page <= 0 ? Math.Max(1, _page) : result.Page;
        _total = result.Total;

        var known = append
            ? new HashSet<string>(_mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先算好每个条目的本地状态，再排序。
        // （原来是在 OrderBy 里顺手调用 ApplyLocalState，排序时状态还是上一轮的，
        //   "可更新置顶"其实一直没生效）
        foreach (var item in result.Items)
        {
            ApplyLocalState(item);
        }

        // 可更新的置顶（保持接口原始顺序，每页内部这样排）
        var incoming = result.Items
            .OrderByDescending(i => i.LocalState == "可更新" ? 1 : 0)
            .Where(i => known.Add(i.Id))
            .ToList();

        // 整批通知 UI，避免一百多条逐个 Add 造成切换来源时卡顿。
        if (append)
        {
            _mods.AddRange(incoming);
        }
        else
        {
            _mods.ReplaceRange(incoming);
        }

        _allLoaded = result.Items.Count == 0 || (_total > 0 && _mods.Count >= _total);

        if (!append)
        {
            UpdateTypeFilter(result.Items);
        }

        UpdateFooter();
        _ = LoadIconsAsync(incoming);

        if (!append && _mods.Count > 0)
        {
            SelectFirstItemDeferred();
        }
    }

    /// <summary>等列表先完成这一轮布局，再加载第一个详情，避免切来源时同时做太多事。</summary>
    private void SelectFirstItemDeferred()
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_mods.Count > 0)
                {
                    ModList.SelectedIndex = 0;
                }
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>资源站：后台把剩余页补齐（会被新的搜索/切源打断）。</summary>
    private async Task FillRemainingSitePagesAsync(
        AdofaiToolsClient client,
        string filterKey,
        string? search,
        string? type,
        string? sort,
        int fillId)
    {
        try
        {
            var rounds = 0;

            while (fillId == _siteFillId && !_allLoaded && rounds++ < 60)
            {
                if (_remoteSource is not null || CurrentFilterKey() != filterKey || _loadedFilter != filterKey)
                {
                    return;
                }

                var next = _page + 1;
                var result = await client.GetModsAsync(next, _pageSize, search, type, null, sort);

                if (fillId != _siteFillId)
                {
                    return;
                }

                var before = _mods.Count;
                ApplySitePage(result, append: true, filterKey);

                if (result.Items.Count == 0 || _mods.Count == before)
                {
                    _allLoaded = true;
                    break;
                }

                SubtitleText.Text =
                    $"{AppServices.SiteDisplayName} 共 {_total} 个资源，已加载 {_mods.Count} / {_total}（后台补全中…）";
            }
        }
        catch (Exception ex)
        {
            AppPaths.AppendDebugLog($"[site] 后台补页失败：{ex.Message}");
        }
        finally
        {
            if (fillId == _siteFillId && _mods.Count > 0)
            {
                UpdateFooter();
                SubtitleText.Text = $"{AppServices.SiteDisplayName} 共 {_total} 个资源，已加载 {_mods.Count} 个。";
            }
        }
    }

    private static readonly SemaphoreSlim IconLoadGate = new(6);

    private static async Task LoadIconsAsync(IEnumerable<ModListItem> items)
    {
        var tasks = items
            .Where(item => item.IconSource is null && !string.IsNullOrWhiteSpace(item.IconUrl))
            .Select(async item =>
            {
                await IconLoadGate.WaitAsync();
                try
                {
                    var source = await ImageLoader.LoadAsync(item.IconUrl);
                    if (source is not null)
                    {
                        item.IconSource = source;
                    }
                }
                finally
                {
                    IconLoadGate.Release();
                }
            });

        await Task.WhenAll(tasks);
    }

    private bool _populatingTypes;

    /// <summary>
    /// 类型筛选是"自适应"的：默认不显示。
    /// 只有当资源站真的返回了多种类型时才出现（对第三方站友好）；
    /// 因为 adofaitools 目前所有资源都是 MOD，这里通常就是隐藏的。
    /// </summary>
    private void UpdateTypeFilter(IReadOnlyList<ModListItem> items)
    {
        var selectedType = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        // 已经在按类型筛选：保持显示，避免把用户"困"在筛选里
        if (!string.IsNullOrWhiteSpace(selectedType))
        {
            TypeCombo.Visibility = Visibility.Visible;
            return;
        }

        var types = items
            .Select(i => i.ResourceType)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (types.Count <= 1)
        {
            TypeCombo.Visibility = Visibility.Collapsed;
            return;
        }

        if (TypeCombo.Items.Count == 0)
        {
            _populatingTypes = true;
            TypeCombo.Items.Add(new ComboBoxItem { Content = "全部类型", Tag = string.Empty });
            foreach (var type in types)
            {
                TypeCombo.Items.Add(new ComboBoxItem { Content = TypeLabel(type), Tag = type });
            }

            TypeCombo.SelectedIndex = 0;
            _populatingTypes = false;
        }

        TypeCombo.Visibility = Visibility.Visible;
    }

    private static string TypeLabel(string type) => type.ToUpperInvariant() switch
    {
        "MOD" => "Mod",
        "RESOURCEPACK" => "资源包",
        "SHADER" => "着色器",
        "PLUGIN" => "插件",
        "LIBRARY" => "库",
        "TOOL" => "工具",
        _ => "其它",
    };

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _populatingTypes)
        {
            return;
        }

        _ = SearchAsync(1);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooter();
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(1);

    private void UpdateFooter()
    {
        LoadMoreButton.IsEnabled = !_busy;

        if (_total <= 0)
        {
            PageText.Text = string.Empty;
            LoadMoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_allLoaded)
        {
            PageText.Text = $"已全部加载（共 {_total} 个）";
            LoadMoreButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PageText.Text = $"已加载 {_mods.Count} / {_total}　（继续向下滚动会自动加载）";
            LoadMoreButton.Visibility = Visibility.Visible;
        }
    }

    // ---------------- 第三方来源（TUF / modlist）----------------

    /// <summary>
    /// 链接一律交给系统浏览器打开。
    /// 注意：WPF 的 Hyperlink 放在 Page/Frame 里时，默认会让**本窗口的 Frame 跳转**（把网页渲染进应用里）。
    /// 所以这里拆成两步：RequestNavigate 一律标记 Handled（阻止跳转），真正打开浏览器放在 Click 里。
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

    /// <summary>
    /// 更新列表上方的“数据来自谁”署名 + 回链。只对第三方来源显示
    /// （TUF 站长要求：使用 TUF 的数据必须 properly credited and linked）。
    /// </summary>
    private void UpdateSourceCredit()
    {
        var remote = (SourceCombo.SelectedItem as SourceChoice)?.Source;

        // ADOFAI Tools 这一项走的是"站点通道"（Source 为 null），
        // 但它是默认来源、用得最多，同样要署名 —— 不能只有第三方有、它没有。
        if (remote is null)
        {
            var siteUrl = AppServices.Settings.Settings.ApiBaseUrl?.Trim();

            if (string.IsNullOrWhiteSpace(siteUrl))
            {
                SourceCreditText.Inlines.Clear();
                SourceCreditText.Visibility = Visibility.Collapsed;
                return;
            }

            ShowSourceCredit($"Mod 数据由 {AppServices.SiteDisplayName} 提供", siteUrl);
            return;
        }

        var remoteSiteUrl = remote.SiteUrl;
        if (string.IsNullOrWhiteSpace(remoteSiteUrl))
        {
            SourceCreditText.Inlines.Clear();
            SourceCreditText.Visibility = Visibility.Collapsed;
            return;
        }

        var label = remote.Id switch
        {
            "tuf" => "Mod 数据与推荐由 TUF（The Universal Forums）提供",
            "modlist" => "Mod 数据由 modlist.org 提供",
            "modrift" => "Mod 数据由 modrift 提供",
            _ => $"Mod 数据由 {remote.DisplayName} 提供",
        };

        ShowSourceCredit(label, remoteSiteUrl);
    }

    /// <summary>把"谁提供的数据 + 站点链接"写进署名行。</summary>
    private void ShowSourceCredit(string label, string siteUrl)
    {
        SourceCreditText.Inlines.Clear();
        SourceCreditText.Inlines.Add(new System.Windows.Documents.Run(label));
        SourceCreditText.Inlines.Add(new System.Windows.Documents.Run("　·　"));

        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(siteUrl))
        {
            NavigateUri = new Uri(siteUrl),
        };

        SourceCreditText.Inlines.Add(link);
        SourceCreditText.Visibility = Visibility.Visible;
    }

    /// <summary>详情面板里的「在源站查看」链接（第三方源用于署名/回链；没有就隐藏）。</summary>
    private void SetModPageLink(string? url)
    {
        ModPageHyperlink.Inlines.Clear();

        if (string.IsNullOrWhiteSpace(url))
        {
            ModPageLink.Visibility = Visibility.Collapsed;
            return;
        }

        ModPageHyperlink.NavigateUri = new Uri(url);
        ModPageHyperlink.Inlines.Add(new System.Windows.Documents.Run(url));
        ModPageLink.Visibility = Visibility.Visible;
    }

    /// <summary>把界面上的排序选项翻成与站点无关的排序方式（各适配器再翻成自己的参数）。</summary>
    private RemoteSort SelectedSort() => ((SortCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "downloads" => RemoteSort.Downloads,
        "favorites" => RemoteSort.Favorites,
        _ => RemoteSort.Updated,
    };

    /// <summary>用来源自己的分类/标签填充「类型」筛选（第三方源用；没有分类就隐藏）。</summary>
    private void UpdateRemoteTypeFilter(IRemoteSource source)
    {
        _populatingTypes = true;
        TypeCombo.Items.Clear();

        if (source.Categories.Count == 0)
        {
            TypeCombo.Visibility = Visibility.Collapsed;
            _populatingTypes = false;
            return;
        }

        TypeCombo.Items.Add(new ComboBoxItem { Content = "全部分类", Tag = string.Empty });
        foreach (var category in source.Categories)
        {
            TypeCombo.Items.Add(new ComboBoxItem { Content = category, Tag = category });
        }

        TypeCombo.SelectedIndex = 0;
        TypeCombo.Visibility = Visibility.Visible;
        _populatingTypes = false;
    }

    /// <summary>来源切换：清空重来（资源站与第三方来源互不干扰）。</summary>
    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _remoteSource = (SourceCombo.SelectedItem as SourceChoice)?.Source;
        UpdateSourceCredit();
        _remoteDetail = null;
        _detail = null;

        _siteFillId++;      // 停掉上一次的后台补页
        _siteRequestId++;   // 让旧资源站请求的结果失效
        _remoteRequestId++; // 让旧第三方源请求的结果失效
        _remoteCts?.Cancel();
        _busy = false;
        _mods.Clear();
        _page = 1;
        _total = 0;
        _allLoaded = false;
        _loadedFilter = string.Empty;

        _detailRequestId++;
        DetailPanel.DataContext = null;
        DetailScroll.Visibility = Visibility.Collapsed;
        DetailEmpty.Text = "从左侧选择一个 mod 查看详情";
        DetailEmpty.Visibility = Visibility.Visible;
        SetModPageLink(null);

        // 收藏对所有来源都生效，切回来时恢复默认状态
        FavoriteButton.IsEnabled = true;
        FavoriteButton.Content = "收藏";
        InstallButton.IsEnabled = true;
        InstallStatusText.Text = string.Empty;

        // 「类型」筛选：先清空，再按新来源重建（资源站那套是"有多个类型才显示"，这里只管第三方）
        _populatingTypes = true;
        TypeCombo.Items.Clear();
        TypeCombo.Visibility = Visibility.Collapsed;
        _populatingTypes = false;

        if (_remoteSource is { } selectedSource)
        {
            UpdateRemoteTypeFilter(selectedSource);
        }

        UpdateFooter();
        _ = SearchAsync(1);
    }

    /// <summary>把一页结果铺到界面上（"缓存里的旧结果"和"刚拉到的新结果"共用）。</summary>
    private void ShowRemotePage(RemoteModPage result, bool append)
    {
        if (!append)
        {
            _page = 1;
            _allLoaded = false;
        }

        _page = Math.Max(1, result.Page);
        _total = result.Total;

        var known = append
            ? new HashSet<string>(_mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incoming = new List<ModListItem>();

        foreach (var remote in result.Items)
        {
            var item = RemoteCatalog.ToListItem(remote);
            ApplyLocalState(item);

            if (known.Add(item.Id))
            {
                incoming.Add(item);
            }
        }

        if (append)
        {
            _mods.AddRange(incoming);
        }
        else
        {
            _mods.ReplaceRange(incoming);
        }

        _allLoaded = result.Items.Count == 0 || (_total > 0 && _mods.Count >= _total);

        UpdateFooter();
        _ = LoadIconsAsync(incoming);

        if (!append && _mods.Count > 0)
        {
            SelectFirstItemDeferred();
        }
    }

    private async Task FetchRemoteAsync(IRemoteSource source, int page, bool append)
    {
        // 每次加载给一个自增号：切换来源 / 重新搜索时，旧的请求作废（否则旧结果会盖回界面上）
        var requestId = ++_remoteRequestId;

        _remoteCts?.Cancel();
        _remoteCts?.Dispose();
        var cts = new CancellationTokenSource();
        _remoteCts = cts;

        if (!append)
        {
            LoadInstalled();
        }

        var filterKey = CurrentFilterKey();
        var search = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        var category = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var sort = SelectedSort();

        var query = new RemoteModQuery(
            Page: Math.Max(1, page),
            PageSize: _pageSize,
            Search: search,
            Category: category,
            Sort: sort);

        // 缓存键：来源 + 查询条件。用来"先显示上次的结果，再后台刷新"，
        // 这样切来源 / 刷新时不会白屏干等（第三方站有时要好几秒）。
        var cacheKey = RemotePageCacheKey(source, query, filterKey);

        var shownFromCache = false;

        if (!append && ApiCache.TryGetStale<RemoteModPage>(cacheKey, out var cached) && cached is { Items.Count: > 0 })
        {
            _loadedFilter = filterKey;
            ShowRemotePage(cached, append: false);
            shownFromCache = true;
            AppPaths.AppendDebugLog($"[remote] {source.Id} 先显示缓存（{cached.Items.Count} 个），后台刷新中");
        }

        SetBusy(true);
        RetryButton.Visibility = Visibility.Collapsed;

        SubtitleText.Text = shownFromCache
            ? $"{source.DisplayName}：已显示上次的结果，正在刷新…"
            : $"正在从 {source.DisplayName} 加载…";

        try
        {
            AppPaths.AppendDebugLog(
                $"[remote] {source.Id} page={page} search={search ?? "-"} sort={sort} category={category ?? "-"}");

            var result = await source.GetModsAsync(query, cts.Token);

            if (requestId != _remoteRequestId)
            {
                // 这次的结果已经过期（用户又换了来源/条件），直接丢掉
                return;
            }

            ApiCache.Set(cacheKey, result);

            _loadedFilter = filterKey;
            ShowRemotePage(result, append);

            SubtitleText.Text = _mods.Count == 0
                ? $"{source.DisplayName}：没有找到匹配的 mod。"
                : $"{source.DisplayName} 共 {result.Total} 个资源，已加载 {_mods.Count} 个。";

            // 首屏出来以后，后台把剩下的页也补齐：
            // 这样往下滚只会"顿一下"，不会"划到底就没了"。
            if (!append && !_allLoaded)
            {
                _ = FillRemainingPagesAsync(source, filterKey, search, category, sort, requestId, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新的一次加载取代了：什么都不做（界面已经由新请求负责）
        }
        catch (Exception ex)
        {
            if (requestId != _remoteRequestId)
            {
                return;
            }

            // 失败时**保留已经显示的内容**（缓存里那份或上一页），只提示 + 给个「重试」
            RetryButton.Visibility = Visibility.Visible;
            AppPaths.AppendDebugLog($"[remote] {source.Id} 加载失败：{ex.GetType().Name} {ex.Message}");
            Report(false, $"从 {source.DisplayName} 获取失败：{ex.Message}");

            SubtitleText.Text = _mods.Count > 0
                ? $"{source.DisplayName}：刷新失败（{ex.Message}）。当前显示的是上次的结果，可点「重试」。"
                : $"{source.DisplayName}：加载失败（{ex.Message}）。可点「重试」。";
        }
        finally
        {
            if (requestId == _remoteRequestId)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>远程列表某一页的缓存键（来源 + 查询条件 + 页码）。</summary>
    private static string RemotePageCacheKey(IRemoteSource source, RemoteModQuery query, string filterKey) =>
        ApiCache.Key(
            "remotepage",
            source.Id,
            query.Page.ToString(),
            query.PageSize.ToString(),
            query.Search,
            query.Category,
            query.Sort.ToString(),
            filterKey);

    /// <summary>
    /// 后台把剩余页补齐（首屏已经显示出来了，这里只是"悄悄补全"）。
    /// - 每补一页就追加进列表、更新"已加载 N / 总数"，用户可以直接滚着看；
    /// - 会被新的加载取消 / 作废（切来源、改搜索条件时）；
    /// - 每页也写进缓存，下次打开能更快铺满。
    /// </summary>
    private async Task FillRemainingPagesAsync(
        IRemoteSource source,
        string filterKey,
        string? search,
        string? category,
        RemoteSort sort,
        int requestId,
        CancellationToken ct)
    {
        try
        {
            var rounds = 0;

            while (!ct.IsCancellationRequested && requestId == _remoteRequestId && !_allLoaded && rounds++ < 60)
            {
                if (CurrentFilterKey() != filterKey || _loadedFilter != filterKey)
                {
                    return;     // 用户改了筛选条件：交给新的那一次加载
                }

                var next = _page + 1;
                var query = new RemoteModQuery(next, _pageSize, search, category, sort);

                AppPaths.AppendDebugLog($"[remote] {source.Id} 后台补页 page={next}");

                var page = await source.GetModsAsync(query, ct);

                if (requestId != _remoteRequestId)
                {
                    return;
                }

                ApiCache.Set(RemotePageCacheKey(source, query, filterKey), page);

                var before = _mods.Count;
                ShowRemotePage(page, append: true);

                if (page.Items.Count == 0 || _mods.Count == before)
                {
                    // 后面没有了（或服务器把末尾页又返回了一遍）：收工
                    _allLoaded = true;
                    break;
                }

                SubtitleText.Text =
                    $"{source.DisplayName} 共 {_total} 个资源，已加载 {_mods.Count} / {_total}（后台补全中…）";
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的加载取代：交给新请求
        }
        catch (Exception ex)
        {
            // 后台补页失败不打扰用户：已经看到的内容还在，滚到底还能手动重试
            AppPaths.AppendDebugLog($"[remote] 后台补页失败：{ex.Message}");
        }
        finally
        {
            if (requestId == _remoteRequestId && _mods.Count > 0)
            {
                UpdateFooter();
                SubtitleText.Text = $"{source.DisplayName} 共 {_total} 个资源，已加载 {_mods.Count} 个。";
            }
        }
    }

    private async Task LoadRemoteDetailAsync(IRemoteSource source, ModListItem item, int requestId)
    {
        var cacheKey = ApiCache.Key("remotedetail", source.Id, item.Slug);
        var fromCache = ApiCache.TryGetStale<RemoteModDetail>(cacheKey, out var cachedDetail) && cachedDetail is not null;
        AppPaths.AppendDebugLog($"[remote] {source.Id} 详情 {item.Slug} 缓存={(fromCache ? "命中" : "未命中")}");

        try
        {
            // ① 先把上次缓存的详情显示出来（点一下就有内容），再后台刷新
            if (fromCache && cachedDetail is not null)
            {
                RenderRemoteDetail(source, item, cachedDetail);
            }
            else
            {
                // 没有缓存：先说清楚"正在加载"，别让右侧空着不动
                DetailEmpty.Text = "正在加载 mod 详情…";
                DetailEmpty.Visibility = Visibility.Visible;
                DetailScroll.Visibility = Visibility.Collapsed;
            }

            var detail = await source.GetModDetailAsync(item.Slug);

            if (requestId != _detailRequestId)
            {
                return;
            }

            if (detail is null)
            {
                ResetDetailPlaceholder();
                Report(false, $"{source.DisplayName} 上没有找到这个 mod 的详情。");
                return;
            }

            ApiCache.Set(cacheKey, detail);
            RenderRemoteDetail(source, item, detail);
        }
        catch (Exception ex)
        {
            if (requestId != _detailRequestId)
            {
                return;
            }

            ResetDetailPlaceholder();
            Report(false, $"加载 {source.DisplayName} 详情失败：{ex.Message}");
        }
    }

    /// <summary>详情还没出来时，右侧恢复成"请选择"的提示。</summary>
    private void ResetDetailPlaceholder()
    {
        if (DetailScroll.Visibility == Visibility.Visible)
        {
            return;     // 已经有内容了（例如缓存那份），保持不动
        }

        DetailEmpty.Text = "从左侧选择一个 mod 查看详情";
        DetailEmpty.Visibility = Visibility.Visible;
    }

    private async void RenderRemoteDetail(IRemoteSource source, ModListItem item, RemoteModDetail detail)
    {
        _remoteDetail = detail;
        _detail = RemoteCatalog.ToDetail(detail);

        DetailPanel.DataContext = _detail;
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        try
        {
            DetailIcon.Source = item.IconSource ?? await ImageLoader.LoadAsync(detail.Mod.IconUrl);
        }
        catch
        {
            // 图标加载失败不影响详情
        }

        // 回链：第三方源优先用它自己的详情页（modlist 的作者希望这样署名），否则用 mod 的主页
        SetModPageLink(source.ModPageUrl(item.Slug) ?? detail.Mod.HomepageUrl);

        PopulateVersions(_detail);

        var melon = detail.Mod.Loader == ModLoader.MelonLoader;
        var gamePath = AppServices.Settings.Settings.GamePath;
        var melonReady = !melon ||
                         (!string.IsNullOrWhiteSpace(gamePath) &&
                          Directory.Exists(gamePath) &&
                          GameLoaderEnvironment.Detect(gamePath).MelonLoaderInstalled);

        // modrift 上混着资源包、着色器、插件等非 mod 条目：它们不是可安装的 Mod 条目。
        var isMod = string.Equals(detail.Mod.ResourceType, "mod", StringComparison.OrdinalIgnoreCase);

        InstallButton.IsEnabled = isMod && melonReady;
        InstallButton.Content = !isMod
            ? "不是 Mod"
            : melon && !melonReady
                ? "需要 MelonLoader"
                : item.LocalState switch
                {
                    "可更新" => "更新",
                    "已安装" => "重新安装",
                    _ => "安装",
                };

        InstallStatusText.Text = !isMod
            ? $"⚠ 这是 {source.DisplayName} 上的「{ResourceTypeLabel(detail.Mod.ResourceType)}」，不是可直接安装的 Mod，Aurora 无法自动安装。可用上面的链接打开原站页面手动下载。"
            : melon && !melonReady
                ? "⚠ 这个 mod 需要 MelonLoader。请先到「加载器」页安装 MelonLoader（或 MelonLoader + UMMBridge），再回来安装。"
                : string.Empty;

        // 收藏对所有来源都生效
        FavoriteButton.IsEnabled = true;
        FavoriteButton.Content = "收藏";
    }

    /// <summary>把 modrift 的 resourceType 翻成用户看得懂的字（其它来源只有 mod，用不到）。</summary>
    private static string ResourceTypeLabel(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "resourcepack" => "资源包 / 预设",
        "shader" => "着色器",
        "plugin" => "插件",
        "library" => "运行库",
        "tool" => "工具",
        "other" => "其它资源",
        _ => "非 Mod 资源",
    };

    private async Task InstallRemoteAsync(IRemoteSource source)
    {
        if (_remoteDetail is null)
        {
            return;
        }

        var service = BuildModService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        var selected = VersionCombo.SelectedItem as ModVersion;
        var version = _remoteDetail.Versions.FirstOrDefault(v =>
                          string.Equals(v.VersionId, selected?.VersionId, StringComparison.OrdinalIgnoreCase))
                      ?? _remoteDetail.Versions.FirstOrDefault();

        if (version is null)
        {
            Report(false, "这个 mod 没有可安装的版本。");
            return;
        }

        SetBusy(true);
        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.Value = 0;

        try
        {
            InstallStatusText.Text = "正在校验下载地址…";

            var progress = new InlineProgress<int>(percent =>
            {
                InstallProgress.Value = percent;
                InstallStatusText.Text = $"正在下载… {percent}%";
            }, Dispatcher);

            var installer = new RemoteInstaller(source, service);
            var result = await installer.InstallAsync(_remoteDetail.Mod, version, progress);

            Report(result.Success, result.Message);
            InstallStatusText.Text = result.Message;

            if (result.Success)
            {
                LoadInstalled(force: true);

                foreach (var mod in _mods)
                {
                    ApplyLocalState(mod);
                }

                InstallButton.Content = "重新安装";
            }
        }
        catch (Exception ex)
        {
            Report(false, $"安装失败：{ex.Message}");
            InstallStatusText.Text = ex.Message;
        }
        finally
        {
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    private void Report(bool success, string message)
    {
        StatusBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = success ? "提示" : "出错了";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private int _detailRequestId;

    private static void DebugLog(string message) => AppPaths.AppendDebugLog("[detail] " + message);

    private async void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModList.SelectedItem is not ModListItem item)
        {
            DebugLog("SelectionChanged: 选中项不是 ModListItem");
            return;
        }

        var requestId = ++_detailRequestId;
        DebugLog($"SelectionChanged -> {item.DisplayName} (req={requestId}, type={item.ResourceType}, slug={item.Slug})");

        // 第三方来源：走自己的详情加载
        if (_remoteSource is { } remote)
        {
            await LoadRemoteDetailAsync(remote, item, requestId);
            return;
        }

        var client = BuildClient(out var error);
        if (client is null)
        {
            Report(false, error);
            return;
        }

        try
        {
            var detailCacheKey = ApiCache.Key("sitedetail", item.ResourceType ?? "MOD", item.Slug);
            var hasCache = ApiCache.TryGetStale<ModDetail>(detailCacheKey, out var cachedDetail) && cachedDetail is not null;

            // ① 有缓存就直接铺出来（点一下马上有内容），再后台刷新
            if (hasCache && cachedDetail is not null)
            {
                RenderSiteDetail(item, cachedDetail);
            }
            else
            {
                // 没有缓存：先说清楚"正在加载"，别让右侧空着不动
                DetailEmpty.Text = "正在加载 mod 详情…";
                DetailEmpty.Visibility = Visibility.Visible;
                DetailScroll.Visibility = Visibility.Collapsed;
            }

            var detail = await client.GetModDetailAsync(item.ResourceType ?? "MOD", item.Slug);

            if (requestId != _detailRequestId)
            {
                DebugLog($"忽略过期请求 req={requestId}（当前 {_detailRequestId}）");
                return;
            }

            ApiCache.Set(detailCacheKey, detail);
            RenderSiteDetail(item, detail);
            DebugLog($"详情已加载 -> {detail.DisplayName}");
        }
        catch (AdofaiToolsException ex)
        {
            DebugLog("AdofaiToolsException: " + ex.Message);
            ResetDetailPlaceholder();
            Report(false, ex.Message);
        }
        catch (Exception ex)
        {
            DebugLog("Exception: " + ex);
            ResetDetailPlaceholder();
            Report(false, $"加载详情失败：{ex.Message}");
        }
    }

    /// <summary>把资源站的一条详情铺到右侧（缓存里的旧详情 / 刚拉到的新详情共用）。</summary>
    private async void RenderSiteDetail(ModListItem item, ModDetail detail)
    {
        _detail = detail;
        DetailPanel.DataContext = detail;
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        try
        {
            DetailIcon.Source = item.IconSource ?? await ImageLoader.LoadAsync(detail.IconUrl);
        }
        catch
        {
            // 图标加载失败不影响详情本身
        }

        SetModPageLink(detail.HomepageUrl ?? detail.SourceUrl);

        PopulateVersions(detail);

        InstallStatusText.Text = string.Empty;
        InstallButton.IsEnabled = true;
        InstallButton.Content = item.LocalState switch
        {
            "可更新" => "更新",
            "已安装" => "重新安装",
            _ => "安装",
        };

        UpdateFavoriteButton(detail.Id);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null)
        {
            return;
        }

        if (_remoteSource is { } remote)
        {
            await InstallRemoteAsync(remote);
            return;
        }

        var client = BuildClient(out var error);
        if (client is null)
        {
            Report(false, error);
            return;
        }

        var service = BuildModService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        var selected = VersionCombo.SelectedItem as ModVersion;
        var file = ModFileSelector.Pick(selected, service.Environment)
                   ?? PreferredFileOf(selected)
                   ?? _detail.PreferredFile;
        if (file is null)
        {
            Report(false, "该 mod 没有可下载的文件。");
            return;
        }

        SetBusy(true);
        InstallButton.IsEnabled = false;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.Value = 0;

        try
        {
            InstallStatusText.Text = "正在获取下载地址…";

            var progress = new InlineProgress<int>(percent =>
            {
                InstallProgress.Value = percent;
                InstallStatusText.Text = $"正在下载… {percent}%";
            }, Dispatcher);

            var installer = new SiteInstaller(client, service);
            var selectedVersion = selected?.VersionId;
            var result = await installer.InstallAsync(_detail, file, selectedVersion, progress);

            Report(result.Success, result.Message);
            InstallStatusText.Text = result.Message;

            LoadInstalled(force: true);
            foreach (var item in _mods)
            {
                ApplyLocalState(item);
            }

            InstallButton.Content = result.Success ? "重新安装" : "安装";
        }
        catch (AdofaiToolsException ex)
        {
            Report(false, ex.Message);
            InstallStatusText.Text = ex.Message;
        }
        finally
        {
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    private void UpdateFavoriteButton(string siteId)
    {
        FavoriteButton.Content = AppServices.Favorites.Contains(siteId) ? "已收藏" : "收藏";
    }

    /// <summary>
    /// 收藏的键：资源站沿用 slug（兼容老数据），第三方源加来源前缀，避免不同源撞名。
    /// </summary>
    private string FavoriteKey(string fallbackId) =>
        _remoteSource is { } source
            ? $"{source.Id}:{_detail?.Slug ?? fallbackId}"
            : fallbackId;

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null)
        {
            return;
        }

        var key = FavoriteKey(_detail.Id);

        if (AppServices.Favorites.Contains(key))
        {
            AppServices.Favorites.Remove(key);
            Report(true, $"已取消收藏「{_detail.DisplayName}」。");
        }
        else
        {
            AppServices.Favorites.Add(new FavoriteMod
            {
                SiteId = key,
                SourceId = _remoteSource?.Id ?? "adofaitools",
                ResourceType = _detail.ResourceType,
                Slug = _detail.Slug,
                DisplayName = _detail.DisplayName,
                Summary = _detail.Summary,
                IconUrl = _detail.IconUrl,
                AuthorsLabel = _detail.AuthorsLabel,
                VersionLabel = string.IsNullOrWhiteSpace(_detail.LatestVersion?.VersionId)
                    ? null
                    : _detail.LatestVersion!.VersionId,
                HomepageUrl = _detail.HomepageUrl,
                SourceUrl = _detail.SourceUrl,
                AddedAt = DateTime.Now,
            });

            Report(true, $"已收藏「{_detail.DisplayName}」，可在左侧「收藏」页查看。");
        }

        UpdateFavoriteButton(key);
    }

    private void PopulateVersions(ModDetail detail)
    {
        var ordered = detail.Versions
            .Where(v => !string.IsNullOrWhiteSpace(v.VersionId))
            .OrderByDescending(v => v.VersionId!, Comparer<string>.Create(KernelService.CompareVersions))
            .ToList();

        VersionCombo.ItemsSource = ordered;
        VersionCombo.SelectedIndex = ordered.Count > 0 ? 0 : -1;
    }

    /// <summary>取某个版本里优先安装的文件（优先 unitymodmanager，其次第一个）。</summary>
    private static ModVersionFile? PreferredFileOf(ModVersion? version)
    {
        if (version is null)
        {
            return null;
        }

        return version.Files.FirstOrDefault(f =>
                   f.Loaders.Any(l => l.Equals("unitymodmanager", StringComparison.OrdinalIgnoreCase)))
               ?? version.Files.FirstOrDefault();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(1);

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadInstalled(force: true);
        _ = SearchAsync(1);
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e) => _ = LoadMoreAsync();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _ = SearchAsync(1);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SearchAsync(1);
        }
    }
}
