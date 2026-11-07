using System.IO;
using System.Xml.Linq;

namespace AdofaiModManager.Services;

/// <summary>
/// 读写 UMM loader 的 Params.xml，用来启用 / 禁用某个 mod。
/// 路径：&lt;游戏&gt;\&lt;游戏&gt;_Data\Managed\UnityModManager\Params.xml
/// </summary>
public sealed class UmmParamsService(string paramsFilePath)
{
    public string ParamsFilePath { get; } = paramsFilePath;

    public bool Exists => File.Exists(ParamsFilePath);

    public bool IsEnabled(string modId, bool defaultValue = true)
    {
        try
        {
            if (!File.Exists(ParamsFilePath))
            {
                return defaultValue;
            }

            var doc = XDocument.Load(ParamsFilePath);
            var mod = FindMod(doc, modId);
            if (mod is null)
            {
                return defaultValue;
            }

            var value = (string?)mod.Attribute("Enabled");
            return value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>一次读出所有 mod 的启用状态（扫描列表时用，避免每个 mod 都解析一遍文件）。</summary>
    public Dictionary<string, bool> ReadAll()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(ParamsFilePath))
            {
                return result;
            }

            var mods = XDocument.Load(ParamsFilePath).Root?.Element("ModParams")?.Elements("Mod");
            foreach (var mod in mods ?? [])
            {
                var id = (string?)mod.Attribute("Id");
                if (string.IsNullOrWhiteSpace(id) || result.ContainsKey(id))
                {
                    continue; // 和 FindMod 一致：同名的以第一条为准
                }

                var value = (string?)mod.Attribute("Enabled");
                result[id] = value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 读不了就当全部启用（与 IsEnabled 的默认值一致）
        }

        return result;
    }

    /// <summary>
    /// 设置某个 mod 的启用状态。
    /// 返回 Params.xml 原本已损坏时备份到的文件名（没损坏返回 null），调用方要告诉用户。
    /// </summary>
    public string? SetEnabled(string modId, bool enabled)
    {
        var doc = LoadOrCreate(out var brokenBackup);
        var root = doc.Root;
        if (root is null)
        {
            root = new XElement("Param");
            doc.Add(root);
        }

        var modParams = root.Element("ModParams");
        if (modParams is null)
        {
            modParams = new XElement("ModParams");
            root.Add(modParams);
        }

        var mod = FindMod(doc, modId);
        if (mod is null)
        {
            mod = new XElement("Mod", new XAttribute("Id", modId));
            modParams.Add(mod);
        }

        mod.SetAttributeValue("Enabled", enabled ? "true" : "false");

        var dir = Path.GetDirectoryName(ParamsFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 原子写：先写临时文件再整体替换，写到一半中断不会留下半截 XML
        var temp = $"{ParamsFilePath}.{Guid.NewGuid():N}.tmp";
        doc.Save(temp);
        File.Move(temp, ParamsFilePath, true);

        return brokenBackup;
    }

    private static XElement? FindMod(XDocument doc, string modId)
    {
        return doc.Root?
            .Element("ModParams")?
            .Elements("Mod")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Id"), modId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 读 Params.xml；不存在就新建。
    /// 解析失败时**先备份**成 Params.xml.broken 再重建：里面除了各 mod 的启用状态，
    /// 还有 UMM 自己的设置（快捷键、窗口位置等），以前直接覆盖就全丢了。
    /// </summary>
    private XDocument LoadOrCreate(out string? brokenBackup)
    {
        brokenBackup = null;

        if (File.Exists(ParamsFilePath))
        {
            try
            {
                return XDocument.Load(ParamsFilePath);
            }
            catch (Exception ex)
            {
                var backup = ParamsFilePath + ".broken";
                if (File.Exists(backup))
                {
                    backup = $"{ParamsFilePath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
                }

                // 备份失败就不要继续重建（宁可这次改不成，也不能把唯一的原文件覆盖掉）
                File.Copy(ParamsFilePath, backup, false);
                brokenBackup = Path.GetFileName(backup);
                AppPaths.AppendDebugLog($"[params] Params.xml 无法解析，已备份为 {brokenBackup} 并重建：{ex.Message}");
            }
        }

        return new XDocument(new XElement("Param", new XElement("ModParams")));
    }
}
