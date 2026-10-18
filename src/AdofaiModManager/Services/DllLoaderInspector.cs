using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AdofaiModManager.Services;

/// <summary>
/// 直接读取 DLL 的 .NET 元数据，判断它是 MelonLoader 还是 BepInEx 的插件/Mod。
/// 不加载程序集，不执行任何代码。
/// </summary>
public static class DllLoaderInspector
{
    public sealed class LoaderFlags
    {
        public bool MelonLoader { get; set; }

        public bool BepInEx { get; set; }

        public bool Any => MelonLoader || BepInEx;
    }

    public static LoaderFlags InspectFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Inspect(stream);
        }
        catch
        {
            return new LoaderFlags();
        }
    }

    public static LoaderFlags Inspect(Stream stream)
    {
        var flags = new LoaderFlags();

        try
        {
            // ZipArchiveEntry.Open() 通常返回不可 seek 的 DeflateStream，
            // PEReader 需要可定位的流，所以先复制到内存。
            if (!stream.CanSeek)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;
                return Inspect(buffer);
            }

            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                return flags;
            }

            var reader = pe.GetMetadataReader();

            foreach (var attributeHandle in reader.CustomAttributes)
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                var typeName = GetAttributeTypeName(reader, attribute.Constructor);
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                if (IsMelonName(typeName))
                {
                    flags.MelonLoader = true;
                }

                if (IsBepInExName(typeName))
                {
                    flags.BepInEx = true;
                }
            }

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                var baseName = GetTypeName(reader, type.BaseType);
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    continue;
                }

                if (IsMelonName(baseName))
                {
                    flags.MelonLoader = true;
                }

                if (IsBepInExName(baseName))
                {
                    flags.BepInEx = true;
                }
            }

            // 即使没有可识别的特性，程序集引用也能作为强证据。
            foreach (var referenceHandle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(referenceHandle).Name);
                if (name.Equals("MelonLoader", StringComparison.OrdinalIgnoreCase))
                {
                    flags.MelonLoader = true;
                }
                else if (name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase))
                {
                    flags.BepInEx = true;
                }
            }
        }
        catch
        {
            // 不是托管 DLL、损坏、或元数据不完整：按无法识别处理
        }

        return flags;
    }

    private static bool IsMelonName(string name) =>
        name.Contains("MelonLoader", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonMod", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonPlugin", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonInfo", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonGame", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonOptionalDependencies", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MelonAdditionalDependencies", StringComparison.OrdinalIgnoreCase);

    private static bool IsBepInExName(string name) =>
        name.Contains("BepInPlugin", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("BepInDependency", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("BepInProcess", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("BaseUnityPlugin", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".BaseUnityPlugin", StringComparison.OrdinalIgnoreCase);

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        try
        {
            switch (constructor.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                    return GetTypeName(reader, method.GetDeclaringType());
                }
                case HandleKind.MemberReference:
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
                    return GetTypeName(reader, member.Parent);
                }
            }
        }
        catch
        {
            // 忽略单个无法解析的特性
        }

        return null;
    }

    private static string? GetTypeName(MetadataReader reader, EntityHandle handle)
    {
        try
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var ns = reader.GetString(type.Namespace);
                    var name = reader.GetString(type.Name);
                    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                }
                case HandleKind.TypeReference:
                {
                    var type = reader.GetTypeReference((TypeReferenceHandle)handle);
                    var ns = reader.GetString(type.Namespace);
                    var name = reader.GetString(type.Name);
                    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }
}
