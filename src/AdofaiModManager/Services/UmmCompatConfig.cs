using System.IO;
using System.Xml.Linq;

namespace AdofaiModManager.Services;

/// <summary>
/// 维护 UMMCompat / UMMBridge 的 UserLibs/Config.xml。
/// 两者的关键约定都是把 UnityModManager 的 ModsDirectory 指向 UMMMods，
/// 避免和 MelonLoader 的 Mods 混在一起。
/// </summary>
public static class UmmCompatConfig
{
    public static void Ensure(string gamePath)
    {
        var dir = Path.Combine(gamePath, "UserLibs");
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "Config.xml");

        try
        {
            if (File.Exists(config))
            {
                var doc = XDocument.Load(config);
                var root = doc.Root;
                if (root is not null)
                {
                    var modsDirectory = root.Element("ModsDirectory");
                    if (modsDirectory is null)
                    {
                        root.Add(new XElement("ModsDirectory", "UMMMods"));
                    }
                    else if (!modsDirectory.Value.Equals("UMMMods", StringComparison.OrdinalIgnoreCase))
                    {
                        modsDirectory.Value = "UMMMods";
                    }

                    doc.Save(config);
                    return;
                }
            }
        }
        catch
        {
            // 配置损坏时重建
        }

        File.WriteAllText(
            config,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Config xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" Name=\"A Dance of Fire and Ice\">\n  <Folder>ADOFAI</Folder>\n  <ModsDirectory>UMMMods</ModsDirectory>\n  <ModInfo>Info.json</ModInfo>\n  <GameExe>A Dance of Fire and Ice.exe</GameExe>\n  <EntryPoint>[UnityEngine.CoreModule.dll]UnityEngine.MonoBehaviour.cctor:Before</EntryPoint>\n  <StartingPoint>[Assembly-CSharp.dll]ADOStartup.Startup:Before</StartingPoint>\n  <UIStartingPoint>[Assembly-CSharp.dll]ADOStartup.Startup:After</UIStartingPoint>\n  <MinimalManagerVersion>0.22.14</MinimalManagerVersion>\n  <Comment>Required minimum game version 2.7.0</Comment>\n</Config>\n");
    }
}
