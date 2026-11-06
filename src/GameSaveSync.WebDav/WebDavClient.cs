using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GameSaveSync.WebDav.Internal;

namespace GameSaveSync.WebDav;

/// <summary>一次 WebDAV 请求的结果。</summary>
public sealed class WebDavResponse
{
    public required HttpStatusCode StatusCode { get; init; }

    public byte[] Content { get; init; } = Array.Empty<byte>();

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;

    public string? ErrorBody
    {
        get
        {
            if (IsSuccess || Content.Length == 0)
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Content);
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>WebDAV 认证失败（401/403），与网络错误区分，避免无意义重试。</summary>
public sealed class WebDavAuthenticationException : Exception
{
    public WebDavAuthenticationException(HttpStatusCode status, string? body)
        : base($"WebDAV 认证失败（{(int)status}），请检查用户名与应用密码。{body}") => StatusCode = status;

    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// WebDAV 客户端（自研，便于控制 ETag / 限流 / 证书固定）。
/// 已知坑的处理：
/// <list type="bullet">
/// <item>MKCOL 不支持递归 → 逐级创建。</item>
/// <item>不返回 ETag → 记录下来由调用方用内容哈希兜底（绝不用 ETag 做内容标识）。</item>
/// <item>HEAD 不被支持 → ExistsAsync 退回 GET + Range: bytes=0-0。</item>
/// <item>不支持 MOVE → PutWithTempNameAsync 自动退化直接 PUT。</item>
/// </list>
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private readonly WebDavOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Uri _baseUri;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public WebDavClient(WebDavOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"WebDAV 地址不是合法绝对 URL：{options.BaseUrl}", nameof(options));
        }

        _baseUri = baseUri;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(CreateHandler(options), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)),
        };

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameSaveSync", "0.1"));
        }
    }

    /// <summary>自签名证书必须由用户显式信任：只接受与本机保存指纹一致的证书。</summary>
    public static HttpMessageHandler CreateHandler(WebDavOptions options)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (!options.UseProxy)
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        var trusted = CertificateTrust.NormalizeThumbprint(options.TrustedCertificateThumbprint);

        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            CertificateTrust.IsTrusted(certificate, errors, trusted);

        return handler;
    }

    private string[] RootSegments =>
        (_options.RemoteRoot ?? string.Empty)
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    public Uri BuildUri(string relativePath) => BuildRawUri(RootSegments.Concat(SplitSegments(relativePath)));

    private Uri BuildRawUri(IEnumerable<string> segments)
    {
        var builder = new StringBuilder(_baseUri.ToString().TrimEnd('/'));
        foreach (var segment in segments)
        {
            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return new Uri(builder.ToString());
    }

    private static IEnumerable<string> SplitSegments(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? Array.Empty<string>()
            : relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

    public Task<WebDavResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken ct,
        params (string Name, string Value)[] headers) =>
        SendCoreAsync(method, BuildUri(relativePath), content, ct, headers);

    private async Task<WebDavResponse> SendCoreAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken ct,
        params (string Name, string Value)[] headers)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, uri) { Content = content };

        if (!string.IsNullOrEmpty(_options.Username))
        {
            var raw = $"{_options.Username}:{_options.Password}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        return new WebDavResponse
        {
            StatusCode = response.StatusCode,
            Content = bytes,
            ETag = response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null),
            LastModified = response.Content.Headers.LastModified,
        };
    }

    /// <summary>逐级 MKCOL（服务端普遍不支持递归创建）。已存在（405）视为成功。</summary>
    public async Task EnsureCollectionAsync(string relativeDirectory, CancellationToken ct)
    {
        var all = RootSegments.Concat(SplitSegments(relativeDirectory ?? string.Empty)).ToList();

        var cumulative = new List<string>();
        foreach (var segment in all)
        {
            cumulative.Add(segment);
            using var empty = new ByteArrayContent(Array.Empty<byte>());
            var response = await SendCoreAsync(WebDavMethods.MkCol, BuildRawUri(cumulative), empty, ct)
                .ConfigureAwait(false);

            // 201 已创建 / 200 已存在 / 405 已存在（部分服务返回）
            if (response.IsSuccess || response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new WebDavAuthenticationException(response.StatusCode, response.ErrorBody);
            }

            throw new WebDavException(
                $"创建目录失败：{string.Join('/', cumulative)}（{(int)response.StatusCode}）", response.StatusCode);
        }
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        var head = await SendAsync(HttpMethod.Head, relativePath, null, ct).ConfigureAwait(false);
        if (head.IsSuccess)
        {
            return true;
        }

        if (head.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        // HEAD 不被支持 → 用 GET + Range 兜底
        var probe = await SendAsync(
            HttpMethod.Get,
            relativePath,
            null,
            ct,
            ("Range", "bytes=0-0")).ConfigureAwait(false);

        return probe.IsSuccess || probe.StatusCode == HttpStatusCode.PartialContent;
    }

    public async Task PutAsync(string relativePath, byte[] content, CancellationToken ct, string? ifMatch = null, string? ifNoneMatch = null)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var headers = new List<(string, string)>();
        if (!string.IsNullOrEmpty(ifMatch))
        {
            headers.Add(("If-Match", ifMatch));
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            headers.Add(("If-None-Match", ifNoneMatch));
        }

        var response = await SendAsync(HttpMethod.Put, relativePath, body, ct, headers.ToArray()).ConfigureAwait(false);
        ThrowIfAuthenticationFailed(response);

        // 服务端 5xx 必须当失败：静默"成功"会让上层以为文件已上传，远端实际缺文件
        if (!response.IsSuccess)
        {
            throw new WebDavException(
                $"上传失败（{(int)response.StatusCode} {response.StatusCode}）：{relativePath}",
                response.StatusCode);
        }
    }

    /// <summary>PUT 字节并返回原始响应（调用方需要看 412/428 做乐观锁）。</summary>
    public async Task<WebDavResponse> PutBytesAsync(
        string relativePath,
        byte[] content,
        CancellationToken ct,
        string? ifMatch = null,
        string? ifNoneMatch = null,
        string contentType = "application/json")
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var headers = new List<(string, string)>();
        if (!string.IsNullOrEmpty(ifMatch))
        {
            headers.Add(("If-Match", ifMatch));
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            headers.Add(("If-None-Match", ifNoneMatch));
        }

        var response = await SendAsync(HttpMethod.Put, relativePath, body, ct, headers.ToArray())
            .ConfigureAwait(false);
        ThrowIfAuthenticationFailed(response);
        return response;
    }

    /// <summary>
    /// 列目录（PROPFIND Depth: 1）。只用于"索引损坏后重建"这类恢复场景，
    /// 正常同步路径绝不逐文件 PROPFIND。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct)
    {
        const string body =
            "<?xml version='1.0' encoding='utf-8'?>" +
            "<D:propfind xmlns:D='DAV:'><D:prop><D:resourcetype/><D:getcontentlength/><D:getlastmodified/></D:prop></D:propfind>";

        using var content = new StringContent(body, Encoding.UTF8, "application/xml");
        var response = await SendAsync(WebDavMethods.PropFind, relativeDirectory, content, ct, ("Depth", "1"))
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<string>();
            }

            throw new WebDavException(
                $"PROPFIND 失败：{relativeDirectory}（{(int)response.StatusCode}）", response.StatusCode);
        }

        var prefix = BuildUri(relativeDirectory).AbsolutePath.TrimEnd('/') + "/";
        var result = new List<string>();

        try
        {
            using var stream = new MemoryStream(response.Content);
            var document = System.Xml.Linq.XDocument.Load(stream);
            foreach (var element in document.Descendants())
            {
                if (!element.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var href = Uri.UnescapeDataString(element.Value.Trim());
                var path = href;
                var schemeIndex = path.IndexOf("://", StringComparison.Ordinal);
                if (schemeIndex >= 0)
                {
                    path = new Uri(path).AbsolutePath;
                }

                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = path[prefix.Length..].TrimEnd('/');
                if (name.Length == 0 || name.Contains('/'))
                {
                    continue;
                }

                result.Add(name);
            }
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }

        return result;
    }

    public async Task<WebDavResponse> PutStreamAsync(string relativePath, Stream content, CancellationToken ct)
    {
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await SendAsync(HttpMethod.Put, relativePath, body, ct).ConfigureAwait(false);
        ThrowIfAuthenticationFailed(response);
        return response;
    }

    /// <summary>
    /// 先写临时名再 MOVE 成正式名（清单用）；服务端不支持 MOVE（405/501）时退化为直接 PUT。
    /// </summary>
    public async Task PutWithTempNameAsync(string relativePath, byte[] content, CancellationToken ct)
    {
        var directory = relativePath.Contains('/')
            ? relativePath[..relativePath.LastIndexOf('/')]
            : string.Empty;
        if (directory.Length > 0)
        {
            await EnsureCollectionAsync(directory, ct).ConfigureAwait(false);
        }

        var tempPath = $"{relativePath}.tmp-{Guid.NewGuid():N}";
        await PutAsync(tempPath, content, ct).ConfigureAwait(false);

        var moved = await TryMoveAsync(tempPath, relativePath, ct).ConfigureAwait(false);
        if (!moved)
        {
            await PutAsync(relativePath, content, ct).ConfigureAwait(false);
            await DeleteAsync(tempPath, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> TryMoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var destination = BuildUri(destinationPath).ToString();
        var response = await SendAsync(
            WebDavMethods.Move,
            sourcePath,
            null,
            ct,
            ("Destination", destination),
            ("Overwrite", "T")).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return false;
        }

        ThrowIfAuthenticationFailed(response);
        throw new WebDavException($"MOVE 失败：{sourcePath} → {destinationPath}", response.StatusCode);
    }

    public async Task<byte[]?> GetAsync(string relativePath, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, relativePath, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfAuthenticationFailed(response);
        if (!response.IsSuccess)
        {
            throw new WebDavException($"GET 失败：{relativePath}（{(int)response.StatusCode}）", response.StatusCode);
        }

        return response.Content;
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Delete, relativePath, null, ct).ConfigureAwait(false);
        if (response.IsSuccess || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        ThrowIfAuthenticationFailed(response);
        throw new WebDavException($"DELETE 失败：{relativePath}", response.StatusCode);
    }

    public static void ThrowIfAuthenticationFailed(WebDavResponse response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new WebDavAuthenticationException(response.StatusCode, response.ErrorBody);
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (!_options.RateLimited)
        {
            return;
        }

        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
            var wait = _options.MinRequestIntervalMs - (int)elapsed.TotalMilliseconds;
            if (wait > 0)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _serial.Release();
        }
    }

    private static string? NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        var cleaned = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
        return cleaned.Length == 0 ? null : cleaned.ToUpperInvariant();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        _serial.Dispose();
    }
}
