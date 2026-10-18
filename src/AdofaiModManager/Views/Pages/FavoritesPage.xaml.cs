using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using Wpf.Ui.Controls;

using AdofaiModManager.Services.Sources;

namespace AdofaiModManager.Views.Pages;

public partial class FavoritesPage : Page
{
    private readonly ObservableCollection<FavoriteMod> _items = [];

    private bool _busy;

    public FavoritesPage()
    {
        InitializeComponent();
        FavList.ItemsSource = _items;
        Loaded += (_, _) =>
        {
            PageScrollFix.DisableOuterPageScrolling(this);
            Reload();
        };
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var favorite in AppServices.Favorites.Items)
        {
            _items.Add(favorite);
        }

        SummaryText.Text = _items.Count == 0 ? "还没有收藏" : $"共 {_items.Count} 个收藏";

        if (_items.Count == 0)
        {
            EmptyHint.Text = "在「在线 Mod」里打开一个 mod，点「收藏」即可加到这里。";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            _ = LoadIconsAsync();
        }
    }

    private async Task LoadIconsAsync()
    {
        var tasks = _items.Select(async item =>
        {
            var source = await ImageLoader.LoadAsync(item.IconUrl);
            if (source is not null)
            {
                item.IconSource = source;
            }
        });

        await Task.WhenAll(tasks);
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

        return ModService.Get(gamePath!);
    }

    private void Report(bool success, string message)
    {
        StatusBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusBar.Title = success ? "提示" : "出错了";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.DataContext is not FavoriteMod favorite)
        {
            return;
        }

        var service = BuildModService();
        if (service is null)
        {
            Report(false, "请先在「设置」里指定游戏目录。");
            return;
        }

        // 第三方来源（TUF / modlist）：走它自己的解析 + 安装通道
        if (RemoteSources.ById(favorite.SourceId) is { } source)
        {
            _busy = true;
            try
            {
                Report(true, $"正在获取「{favorite.DisplayName}」…");

                var remoteDetail = await source.GetModDetailAsync(favorite.Slug);
                if (remoteDetail is null)
                {
                    Report(false, $"在 {source.DisplayName} 上找不到这个 mod。");
                    return;
                }

                var version = remoteDetail.Versions.FirstOrDefault(v =>
                                  string.Equals(v.VersionId, favorite.VersionLabel, StringComparison.OrdinalIgnoreCase))
                              ?? remoteDetail.Versions.FirstOrDefault();

                if (version is null)
                {
                    Report(false, "这个 mod 没有可安装的版本。");
                    return;
                }

                var result = await new RemoteInstaller(source, service).InstallAsync(remoteDetail.Mod, version);
                Report(result.Success, result.Message);
            }
            catch (Exception ex)
            {
                Report(false, ex.Message);
            }
            finally
            {
                _busy = false;
            }

            return;
        }

        var client = BuildClient(out var error);
        if (client is null)
        {
            Report(false, error);
            return;
        }

        _busy = true;
        try
        {
            Report(true, $"正在获取「{favorite.DisplayName}」…");
            var detail = await client.GetModDetailAsync(favorite.ResourceType ?? "MOD", favorite.Slug);
            var installer = new SiteInstaller(client, service);
            var result = await installer.InstallAsync(detail);
            Report(result.Success, result.Message);
        }
        catch (AdofaiToolsException ex)
        {
            Report(false, ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FavoriteMod favorite)
        {
            return;
        }

        var url = favorite.HomepageUrl
                  ?? favorite.SourceUrl
                  ?? AppServices.Settings.Settings.ApiBaseUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report(false, $"打开链接失败：{ex.Message}");
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FavoriteMod favorite)
        {
            return;
        }

        AppServices.Favorites.Remove(favorite.SiteId);
        Report(true, $"已取消收藏「{favorite.DisplayName}」。");
        Reload();
    }
}
