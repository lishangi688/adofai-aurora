namespace AdofaiModManager.Services;

/// <summary>加载器组合的兼容规则（UI 与安装器共用）。</summary>
public static class LoaderSelectionRules
{
    public static string? Validate(LoaderSelection selection)
    {
        if (!selection.Umm && !selection.MelonLoader && !selection.BepInEx)
        {
            return "请至少选择一个加载器。";
        }

        if (selection.MelonLoader && selection.BepInEx)
        {
            return "MelonLoader 与 BepInEx 的注入方式冲突，不能同时安装。";
        }

        if (selection.Umm && selection.BepInEx)
        {
            return "UnityModManager 与 BepInEx 都使用 Doorstop 注入，不能同时安装。";
        }

        if (selection.Umm && selection.MelonLoader && !selection.UmmCompat)
        {
            return "UnityModManager 与 MelonLoader 同时使用会冲突，请勾选 UMMBridge 兼容层。";
        }

        if (selection.UmmCompat && !(selection.Umm && selection.MelonLoader))
        {
            return "UMMBridge 只能在 MelonLoader + UnityModManager 的组合下使用。";
        }

        return null;
    }
}

