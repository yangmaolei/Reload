using System.Net;

namespace GameSaveSync.Core.Abstractions;

/// <summary>
/// 提供 HTTP 代理设置。
/// <para>
/// 为什么需要它：.NET 的 <see cref="HttpClient"/> 默认会优先读环境变量
/// （<c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>），其次才读 Windows 的系统代理。
/// 在装了代理软件的机器上，环境变量里往往躺着一个只覆盖部分域名的代理，
/// 于是本来能直连的站点也会被绕到那个代理上去、被对方的防护拦成 403。
/// </para>
/// <para>
/// 实现方（Infrastructure 层）负责读注册表里的系统代理，
/// Core 只依赖这个抽象，不直接碰注册表。
/// </para>
/// </summary>
public interface IWebProxyProvider
{
    /// <summary>
    /// 返回要使用的代理；返回 null 表示直连（不用代理）。
    /// </summary>
    IWebProxy? GetProxy();
}
