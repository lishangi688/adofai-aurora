using System.IO;
using System.Text.Json;
using AdofaiModManager.Models;

namespace AdofaiModManager.Services;

/// <summary>
/// 读写本地配置。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
    };

    public string ConfigDirectory { get; }

    public string ConfigFilePath { get; }

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdofaiModManager");
        ConfigFilePath = Path.Combine(ConfigDirectory, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Settings = new AppSettings();

            // 读不出来的配置先留一份备份再说：下一次 Save 会用默认值覆盖它，
            // 不备份的话用户填过的 API key、游戏目录就彻底找不回来了
            try
            {
                File.Copy(ConfigFilePath, ConfigFilePath + ".broken", true);
            }
            catch
            {
                // 忽略
            }

            AppPaths.AppendDebugLog($"[settings] 配置文件损坏，已备份为 settings.json.broken 并使用默认配置：{ex.Message}");
        }

        // 旧版本可能存过 "c:/program files (x86)/steam\steamapps\..." 这种混合写法的游戏目录
        //（来自 Steam 注册表），读进来时顺手纠正，下次保存就写回规范写法
        if (!string.IsNullOrWhiteSpace(Settings.GamePath))
        {
            Settings.GamePath = SteamGameLocator.NormalizePath(Settings.GamePath);
        }

        // 诊断日志开关跟设置走（默认开）
        AppPaths.DiagnosticLogEnabled = Settings.DiagnosticLogging;
    }

    public void Save()
    {
        try
        {
            AppPaths.WriteAllTextAtomic(ConfigFilePath, JsonSerializer.Serialize(Settings, JsonWriteOptions));
        }
        catch
        {
            // 配置保存失败不应导致程序崩溃
        }
    }
}
