using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using AdofaiModManager.Models;
using Wpf.Ui.Controls;

namespace AdofaiModManager.Views.Dialogs;

/// <summary>
/// 更新一个已有 Mod 时，如果目标版本提供多个文件（例如 AETC 的基础包 / 完整包），
/// 让用户选择实际要装的那一个。选择会按“去掉版本号的文件名”记住。
/// </summary>
public partial class ModFileChoiceDialog : FluentWindow
{
    public ModFileChoiceDialog(
        string modName,
        string? version,
        IReadOnlyList<ModVersionFile> files,
        string? preferredKey)
    {
        InitializeComponent();

        HeadlineText.Text = string.IsNullOrWhiteSpace(version)
            ? $"{modName}：请选择要更新的文件"
            : $"{modName} → {version}：请选择要更新的文件";

        FileList.ItemsSource = files;

        var selected = files.FirstOrDefault(f =>
                           string.Equals(VariantKey(f.Name, version), preferredKey, StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault();

        FileList.SelectedItem = selected;
    }

    public ModVersionFile? Selected => FileList.SelectedItem as ModVersionFile;

    /// <summary>
    /// 把文件名里的版本号去掉，得到稳定的“文件变体”标识。
    /// 例：AETC-1.1.1-Full.zip → AETC--Full.zip，下个版本 1.1.2 仍能匹配。
    /// </summary>
    public static string VariantKey(string? fileName, string? versionId)
    {
        var key = (fileName ?? string.Empty).Trim();
        if (key.Length == 0 || string.IsNullOrWhiteSpace(versionId))
        {
            return key;
        }

        return Regex.Replace(key, Regex.Escape(versionId.Trim()), string.Empty, RegexOptions.IgnoreCase);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
