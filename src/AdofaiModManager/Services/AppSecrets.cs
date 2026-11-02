namespace AdofaiModManager.Services;

/// <summary>
/// 随客户端一起分发的公开凭据。
///
/// 这里的 ADOFAI Tools key 是站长提供给 Aurora 的专用 key，会出现在公开仓库和发布包里，
/// 因此**不要把它当成秘密**：它只用于默认站点的只读接口和下载，泄漏后可以在站点后台撤销。
/// 用户可以在「设置」里填写自己的 key 覆盖它。
/// </summary>
internal static class AppSecrets
{
    /// <summary>
    /// Aurora 专用的 ADOFAI Tools 公共 API key。
    /// 留空表示不内置（行为退回“必须由用户填写 key”）。
    /// </summary>
    internal const string AdofaiToolsApiKey = "adof_sk_-g9Z95D3rfQHOb5eO6zW00kQERqQIDa1xF-bkialLDY";
}
