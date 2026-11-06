using System.Net;

namespace GameSaveSync.WebDav.Internal;

/// <summary>WebDAV 协议层错误（非认证问题）。</summary>
public sealed class WebDavException : Exception
{
    public WebDavException(string message, HttpStatusCode statusCode) : base(message) => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}

internal static class WebDavMethods
{
    public static readonly HttpMethod MkCol = new("MKCOL");
    public static readonly HttpMethod Move = new("MOVE");
    public static readonly HttpMethod PropFind = new("PROPFIND");
}
