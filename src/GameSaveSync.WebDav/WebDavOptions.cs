namespace GameSaveSync.WebDav;

/// <summary>远端服务商类型：决定用哪种上传策略。</summary>
public enum WebDavProvider
{
    Generic = 0,
    Nextcloud = 1,
}

/// <summary>
/// WebDAV 连接参数。<see cref="TrustedCertificateThumbprint"/> 只在用户显式信任自签名证书后才有值，
/// 且必须是 SHA-256 指纹 —— 不允许全局跳过证书校验。
/// </summary>
public sealed class WebDavOptions
{
    /// <summary>服务入口，如 <c>https://dav.jianguoyun.com/dav/</c>。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>远端根目录（在 BaseUrl 之下），默认 GameSaveSync。</summary>
    public string RemoteRoot { get; set; } = GameSaveSync.Core.Configuration.AppPaths.DefaultRemoteRoot;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>已信任的自签名证书 SHA-256 指纹（十六进制，可带空格/冒号）。</summary>
    public string? TrustedCertificateThumbprint { get; set; }

    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>是否使用系统代理。测试/内网直连时可关闭。</summary>
    public bool UseProxy { get; set; } = true;

    /// <summary>服务商类型（Nextcloud 会启用分块上传大对象）。</summary>
    public WebDavProvider Provider { get; set; } = WebDavProvider.Generic;

    /// <summary>分块上传的片大小（默认 8 MB）。</summary>
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>是否允许（串行）限流模式。坚果云这类服务必须开。</summary>
    public bool RateLimited { get; set; }

    /// <summary>限流模式下的最小请求间隔（毫秒）。</summary>
    public int MinRequestIntervalMs { get; set; } = 350;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);

    public WebDavOptions Clone() => (WebDavOptions)MemberwiseClone();
}
