using System.Diagnostics;

namespace AdofaiModManager.Services;

/// <summary>
/// 用系统默认程序打开外部链接（浏览器 / 邮件），**只放行 http / https / mailto**。
///
/// mod 简介、更新说明、主页地址都来自第三方站点，内容不可信；而 ShellExecute 会把任何协议
/// 交给对应的处理程序 —— 一个 file://\\host\share\x.exe 或 search-ms: 链接，点一下就会
/// 打开远程共享或触发系统功能。所以凡是打开"不是我们自己写死的"地址，都走这里。
/// </summary>
public static class ExternalLinks
{
    /// <summary>是不是允许打开的链接（绝对地址，协议为 http / https / mailto）。</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && IsAllowed(uri);

    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);

    /// <summary>打开链接；协议不在白名单里时抛 <see cref="InvalidOperationException"/>（调用方按原来的方式提示失败）。</summary>
    public static void Open(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || !IsAllowed(uri))
        {
            AppPaths.AppendDebugLog($"[link] 已拦截不支持的链接：{url}");
            throw new InvalidOperationException("只能打开 http / https 网页链接。");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
