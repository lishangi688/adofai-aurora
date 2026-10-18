using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using AdofaiModManager.Models;
using AdofaiModManager.Services;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Dialogs;

public partial class LoaderSelectionDialog : FluentWindow
{
    private readonly string _gamePath;
    private readonly GameLoaderEnvironment _environment;
    private readonly ObservableCollection<LoaderOptionItem> _options = [];
    private bool _loading = true;
    private bool _busy;

    public LoaderSelectionDialog(string gamePath)
    {
        InitializeComponent();

        _gamePath = gamePath;
        _environment = GameLoaderEnvironment.Detect(gamePath);

        _options.Add(new LoaderOptionItem
        {
            Kind = ModLoaderKind.UnityModManager,
            Title = "UnityModManager",
            Badge = "UMM",
            Description = "经典 Mod 加载器，使用 Info.json。",
            IsSelected = _environment.UmmInjectionInstalled,
        });

        _options.Add(new LoaderOptionItem
        {
            Kind = ModLoaderKind.MelonLoader,
            Title = "MelonLoader",
            Badge = "ML",
            Description = "现代 Mod 加载器，支持 Mods / Plugins / UserLibs。",
            IsSelected = _environment.MelonLoaderInstalled &&
                          _environment.ActiveInjection is ModLoaderKind.MelonLoader or ModLoaderKind.Unknown,
        });

        _options.Add(new LoaderOptionItem
        {
            Kind = ModLoaderKind.BepInEx,
            Title = "BepInEx",
            Badge = "BepInEx",
            Description = "插件加载器，使用 BepInEx/plugins。",
            IsSelected = _environment.BepInExInstalled &&
                          _environment.ActiveInjection == ModLoaderKind.BepInEx,
        });

        // 已经处于 UMMBridge 环境时，把 UMM + ML 都选中。
        if (_environment.UmmCompatEnabled)
        {
            _options.First(o => o.Kind == ModLoaderKind.UnityModManager).IsSelected = true;
            _options.First(o => o.Kind == ModLoaderKind.MelonLoader).IsSelected = true;
            CompatCheck.IsChecked = true;
        }

        LoaderList.ItemsSource = _options;
        UpdateCompatibility();

        Loaded += async (_, _) => await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            ProgressText.Text = "正在获取加载器版本列表…";

            var ummTask = LoaderCatalogService.GetUmmVersionsAsync(_gamePath);
            var mlTask = LoaderCatalogService.GetMelonLoaderVersionsAsync();
            var bepTask = LoaderCatalogService.GetBepInExVersionsAsync();
            var compatTask = LoaderCatalogService.GetUmmCompatVersionsAsync();

            await Task.WhenAll(ummTask, mlTask, bepTask, compatTask);

            FillVersions(ModLoaderKind.UnityModManager, await ummTask, _environment.UmmVersion);
            FillVersions(ModLoaderKind.MelonLoader, await mlTask, _environment.MelonLoaderVersion);
            FillVersions(ModLoaderKind.BepInEx, await bepTask, _environment.BepInExVersion);

            var compatVersions = await compatTask;
            CompatVersionCombo.ItemsSource = compatVersions;
            CompatVersionCombo.SelectedItem = compatVersions.FirstOrDefault(v =>
                string.Equals(v.Version, _environment.UmmCompatVersion, StringComparison.OrdinalIgnoreCase))
                ?? compatVersions.FirstOrDefault();

            _loading = false;
            UpdateCompatibility();
        }
        catch (Exception ex)
        {
            _loading = false;
            ProgressText.Text = "版本列表获取失败，仍可使用内置版本。";
            AppPaths.AppendDebugLog($"[loader] 加载版本列表失败：{ex}");
            UpdateCompatibility();
        }
    }

    private void FillVersions(ModLoaderKind kind, IReadOnlyList<LoaderVersionOption> versions, string? currentVersion)
    {
        var option = _options.FirstOrDefault(o => o.Kind == kind);
        if (option is null)
        {
            return;
        }

        option.Versions.Clear();
        foreach (var version in versions)
        {
            option.Versions.Add(version);
        }

        option.SelectedVersion = versions.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(currentVersion) &&
            string.Equals(v.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
    }

    private void Loader_Changed(object sender, RoutedEventArgs e) => UpdateCompatibility();

    private void Compat_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateCompatibility();
    }

    private void Version_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateCompatibility();
        }
    }

    private void UpdateCompatibility()
    {
        var umm = _options.First(o => o.Kind == ModLoaderKind.UnityModManager);
        var ml = _options.First(o => o.Kind == ModLoaderKind.MelonLoader);
        var bep = _options.First(o => o.Kind == ModLoaderKind.BepInEx);

        var needsCompat = umm.IsSelected && ml.IsSelected;
        CompatPanel.Visibility = needsCompat ? Visibility.Visible : Visibility.Collapsed;
        CompatCheck.IsEnabled = needsCompat && !_loading && !_busy;

        if (!needsCompat && CompatCheck.IsChecked == true)
        {
            CompatCheck.IsChecked = false;
        }

        umm.StatusText = string.Empty;
        ml.StatusText = string.Empty;
        bep.StatusText = string.Empty;

        var valid = true;
        var hint = string.Empty;

        if (umm.IsSelected && bep.IsSelected)
        {
            umm.StatusText = "与 BepInEx 不兼容：二者都使用 Doorstop 注入。";
            bep.StatusText = "与 UnityModManager 不兼容。";
            valid = false;
            hint = "UMM 与 BepInEx 不能同时安装，请取消其中一个。";
        }
        else if (ml.IsSelected && bep.IsSelected)
        {
            ml.StatusText = "与 BepInEx 不兼容。";
            bep.StatusText = "与 MelonLoader 不兼容。";
            valid = false;
            hint = "MelonLoader 与 BepInEx 不能同时安装，请取消其中一个。";
        }
        else if (needsCompat && CompatCheck.IsChecked != true)
        {
            umm.StatusText = "与 MelonLoader 共存需要 UMMBridge。";
            ml.StatusText = "与 UMM 共存需要 UMMBridge。";
            valid = false;
            hint = "勾选下方的 UMMBridge 后才能一起安装。";
        }
        else if (!umm.IsSelected && !ml.IsSelected && !bep.IsSelected)
        {
            valid = false;
            hint = "请至少选择一个加载器。";
        }
        else if (needsCompat)
        {
            hint = "兼容模式：UMM 由 UMMBridge 加载，UMM mod 放在 UMMMods。";
        }

        var ruleError = LoaderSelectionRules.Validate(new LoaderSelection
        {
            Umm = umm.IsSelected,
            MelonLoader = ml.IsSelected,
            BepInEx = bep.IsSelected,
            UmmCompat = CompatCheck.IsChecked == true,
        });

        if (valid && ruleError is not null)
        {
            valid = false;
            hint = ruleError;
        }

        if (_loading)
        {
            valid = false;
            hint = "正在获取版本列表…";
        }

        var versionsReady =
            (!umm.IsSelected || umm.SelectedVersion is not null) &&
            (!ml.IsSelected || ml.SelectedVersion is not null) &&
            (!bep.IsSelected || bep.SelectedVersion is not null) &&
            (!needsCompat || CompatVersionCombo.SelectedItem is not null);

        InstallButton.IsEnabled = !_busy && valid && versionsReady;
        ProgressText.Text = _busy ? ProgressText.Text : hint;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var umm = _options.First(o => o.Kind == ModLoaderKind.UnityModManager);
        var ml = _options.First(o => o.Kind == ModLoaderKind.MelonLoader);
        var bep = _options.First(o => o.Kind == ModLoaderKind.BepInEx);
        var compat = umm.IsSelected && ml.IsSelected && CompatCheck.IsChecked == true;

        var selection = new LoaderSelection
        {
            Umm = umm.IsSelected,
            MelonLoader = ml.IsSelected,
            BepInEx = bep.IsSelected,
            UmmCompat = compat,
            UmmVersion = umm.SelectedVersion,
            MelonLoaderVersion = ml.SelectedVersion,
            BepInExVersion = bep.SelectedVersion,
            UmmCompatVersion = CompatVersionCombo.SelectedItem as LoaderVersionOption,
        };

        SetBusy(true);
        try
        {
            var progress = new InlineProgress<string>(
                text =>
                {
                    ProgressText.Text = text;
                    if (!text.StartsWith("正在下载", StringComparison.Ordinal))
                    {
                        InstallProgress.IsIndeterminate = true;
                    }
                },
                Dispatcher);

            var downloadProgress = new Progress<DownloadProgress>(downloaded =>
            {
                InstallProgress.Visibility = Visibility.Visible;
                InstallProgress.IsIndeterminate = downloaded.Percent < 0;
                if (downloaded.Percent >= 0)
                {
                    InstallProgress.Value = downloaded.Percent;
                    ProgressText.Text = $"正在下载… {downloaded.Percent}%";
                }
            });

            var result = await LoaderInstallerService.ApplyAsync(
                _gamePath,
                selection,
                progress,
                downloadProgress);

            if (result.Success)
            {
                StatusBar.Severity = InfoBarSeverity.Success;
                StatusBar.Title = "安装完成";
                StatusBar.Message = result.Message;
                StatusBar.IsOpen = true;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.Title = "安装失败";
                StatusBar.Message = result.Message;
                StatusBar.IsOpen = true;
                ProgressText.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "安装失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
        finally
        {
            SetBusy(false);
            UpdateCompatibility();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.IsIndeterminate = busy;
        InstallProgress.Value = 0;
        ProgressText.Text = busy ? "正在安装，请不要启动游戏…" : string.Empty;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class LoaderOptionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private LoaderVersionOption? _selectedVersion;
        private string _statusText = string.Empty;

        public required ModLoaderKind Kind { get; init; }

        public required string Title { get; init; }

        public required string Description { get; init; }

        public required string Badge { get; init; }

        public ObservableCollection<LoaderVersionOption> Versions { get; } = [];

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public LoaderVersionOption? SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                if (Equals(_selectedVersion, value))
                {
                    return;
                }

                _selectedVersion = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}


