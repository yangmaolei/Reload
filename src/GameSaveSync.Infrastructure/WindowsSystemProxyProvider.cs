using System.Net;
using GameSaveSync.Core.Abstractions;
using Microsoft.Win32;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// 从 Windows 注册表读取系统代理设置。
/// <para>
/// 读 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings</c> 的
/// <c>ProxyEnable</c> 与 <c>ProxyServer</c>。只支持"一个地址给所有协议"的简单写法
/// （<c>127.0.0.1:7890</c> 或 <c>http=…;https=…</c>），
/// PAC 脚本（<c>AutoConfigURL</c>）不处理 —— 那需要拉一次 PAC 才知道结果，
/// 对本工具的用途（访问几个固定的公开数据源）不划算。
/// </para>
/// </summary>
public sealed class WindowsSystemProxyProvider : IWebProxyProvider
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public IWebProxy? GetProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            if (key is null)
            {
                return null;
            }

            var enabled = key.GetValue("ProxyEnable");
            if (enabled is not int flag || flag == 0)
            {
                return null;
            }

            var server = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            var address = ResolveAddress(server);
            if (address is null)
            {
                return null;
            }

            return new WebProxy(address);
        }
        catch
        {
            // 读不到就用默认行为
            return null;
        }
    }

    /// <summary>
    /// 把 ProxyServer 的各种写法解析成一个 URI。
    /// 支持：<c>host:port</c>、<c>http=host:port;https=host:port</c>、<c>socks=host:port</c>。
    /// </summary>
    private static Uri? ResolveAddress(string server)
    {
        var text = server.Trim();

        if (!text.Contains('='))
        {
            return BuildUri(text, "http");
        }

        Uri? fallback = null;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var scheme = part[..separator].Trim().ToLowerInvariant();
            var address = part[(separator + 1)..].Trim();

            if (scheme == "https")
            {
                return BuildUri(address, "http");
            }

            fallback ??= BuildUri(address, "http");
        }

        return fallback;
    }

    private static Uri? BuildUri(string address, string defaultScheme)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        // 没写 scheme 的补上；socks5 之类保持原样
        var candidate = address.Contains("://", StringComparison.Ordinal)
            ? address
            : $"{defaultScheme}://{address}";

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;
    }
}
