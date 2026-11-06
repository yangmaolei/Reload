using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GameSaveSync.Tests.TestSupport;

/// <summary>
/// 进程内极简 WebDAV 服务器（TcpListener 手写 HTTP/1.1，避免 HttpListener 的 URL ACL 麻烦）。
/// 支持 MKCOL / PUT / GET / HEAD / DELETE / MOVE，支持 chunked 请求体与基础认证，
/// 并可注入故障（固定返回 5xx）以验证"上传失败但本地快照不丢"。
/// </summary>
public sealed class InMemoryWebDavServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _etags = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _collections = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public InMemoryWebDavServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _collections[""] = 1;
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/dav/";

    /// <summary>非 0 时所有 PUT 返回该状态码（模拟服务端故障）。</summary>
    public int FailPutStatus { get; set; }

    /// <summary>非 0 时所有请求返回该状态码（模拟整站故障）。</summary>
    public int FailAllStatus { get; set; }

    /// <summary>模拟"PUT 之前别的设备刚改过 index.json"：下一次写索引返回 412。</summary>
    public int FailIndexPutOnce { get; set; }

    /// <summary>直接改远端文件内容（模拟篡改/截断/别的设备写入），ETag 同步更新。</summary>
    public void Overwrite(string relativePath, byte[] content)
    {
        var key = Normalize(relativePath);
        _files[key] = content;
        _etags[key] = ComputeEtag(content);
    }

    public void Remove(string relativePath)
    {
        var key = Normalize(relativePath);
        _files.TryRemove(key, out _);
        _etags.TryRemove(key, out _);
    }

    public bool RequireAuth { get; set; }

    public string? ExpectedAuthorization { get; set; }

    public int PutCount;

    public int RequestCount;

    private readonly ConcurrentQueue<string> _requestLog = new();

    /// <summary>"METHOD key" 序列，用于断言上传顺序（对象先、清单后）。</summary>
    public IReadOnlyList<string> RequestLog => _requestLog.ToArray();

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public IReadOnlyCollection<string> Collections => _collections.Keys.ToArray();

    public byte[]? Get(string relativePath) =>
        _files.TryGetValue(Normalize(relativePath), out var bytes) ? bytes : null;

    public bool HasCollection(string relativePath) => _collections.ContainsKey(Normalize(relativePath));

    public string? GetText(string relativePath)
    {
        var bytes = Get(relativePath);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        _files.Keys.Where(k => k.StartsWith(Normalize(prefix), StringComparison.Ordinal)).OrderBy(k => k).ToList();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(client));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 15000;
                await using var stream = client.GetStream();
                var request = await HttpRequest.ReadAsync(stream, _cts.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                Interlocked.Increment(ref RequestCount);
                _requestLog.Enqueue($"{request.Method} {Normalize(request.Path)}");
                var (status, headers, body) = Process(request);
                await WriteResponseAsync(stream, status, headers, body, request.Method).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 测试服务器：忽略单个连接的异常
            }
        }
    }

    private (int Status, List<(string, string)> Headers, byte[] Body) Process(HttpRequest request)
    {
        var headers = new List<(string, string)>();

        if (FailAllStatus != 0)
        {
            return (FailAllStatus, headers, Array.Empty<byte>());
        }

        if (RequireAuth)
        {
            if (!request.Headers.TryGetValue("Authorization", out var auth) ||
                (ExpectedAuthorization is not null &&
                 !string.Equals(auth, ExpectedAuthorization, StringComparison.Ordinal)))
            {
                headers.Add(("WWW-Authenticate", "Basic realm=\"test\""));
                return (401, headers, Encoding.UTF8.GetBytes("unauthorized"));
            }
        }

        var key = Normalize(request.Path);

        switch (request.Method)
        {
            case "MKCOL":
                if (_collections.ContainsKey(key))
                {
                    return (405, headers, Array.Empty<byte>());
                }

                _collections[key] = 1;
                return (201, headers, Array.Empty<byte>());

            case "PUT":
                if (key.EndsWith("index.json", StringComparison.Ordinal) && FailIndexPutOnce > 0)
                {
                    FailIndexPutOnce--;
                    return (412, headers, Encoding.UTF8.GetBytes("precondition failed (injected)"));
                }

                if (FailPutStatus != 0)
                {
                    return (FailPutStatus, headers, Encoding.UTF8.GetBytes("put failed"));
                }

                // 乐观锁：If-None-Match: * 要求文件不存在；If-Match 要求 ETag 一致
                if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch) &&
                    ifNoneMatch.Trim() == "*" && _files.ContainsKey(key))
                {
                    return (412, headers, Encoding.UTF8.GetBytes("precondition failed"));
                }

                if (request.Headers.TryGetValue("If-Match", out var ifMatch))
                {
                    if (!_etags.TryGetValue(key, out var currentEtag) ||
                        (!ifMatch.Trim().Equals("*", StringComparison.Ordinal) &&
                         !ifMatch.Trim().Equals(currentEtag, StringComparison.Ordinal)))
                    {
                        return (412, headers, Encoding.UTF8.GetBytes("precondition failed"));
                    }
                }

                Interlocked.Increment(ref PutCount);
                var existed = _files.ContainsKey(key);
                _files[key] = request.Body;
                var etag = ComputeEtag(request.Body);
                _etags[key] = etag;
                headers.Add(("ETag", etag));
                return (existed ? 204 : 201, headers, Array.Empty<byte>());

            case "GET":
                if (!_files.TryGetValue(key, out var content))
                {
                    return (404, headers, Array.Empty<byte>());
                }

                // ETag 以内容为准；缺失时补登记，保证后续 If-Match 能匹配
                var contentEtag = ComputeEtag(content);
                _etags[key] = contentEtag;
                headers.Add(("ETag", contentEtag));

                if (request.Headers.TryGetValue("If-None-Match", out var inm) &&
                    inm.Trim().Equals(contentEtag, StringComparison.Ordinal))
                {
                    return (304, headers, Array.Empty<byte>());
                }

                if (request.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=0-0", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add(("Content-Range", $"bytes 0-0/{content.Length}"));
                    return (206, headers, content.Length == 0 ? Array.Empty<byte>() : new[] { content[0] });
                }

                if (_etags.TryGetValue(key, out var existingEtag))
                {
                    headers.Add(("ETag", existingEtag));
                }

                return (200, headers, content);

            case "HEAD":
                if (!_files.TryGetValue(key, out var headContent))
                {
                    return (404, headers, Array.Empty<byte>());
                }

                headers.Add(("Content-Length", headContent.Length.ToString()));
                headers.Add(("ETag", _etags.TryGetValue(key, out var headEtag) ? headEtag : ComputeEtag(headContent)));
                return (200, headers, Array.Empty<byte>());

            case "DELETE":
                if (_files.TryRemove(key, out _))
                {
                    _etags.TryRemove(key, out _);
                    return (204, headers, Array.Empty<byte>());
                }

                if (_collections.TryRemove(key, out _))
                {
                    return (204, headers, Array.Empty<byte>());
                }

                return (404, headers, Array.Empty<byte>());

            case "MOVE":
                if (!request.Headers.TryGetValue("Destination", out var destination))
                {
                    return (400, headers, Array.Empty<byte>());
                }

                var destinationKey = Normalize(new Uri(destination).AbsolutePath.TrimStart('/'));

                // Nextcloud 分块上传的收尾：MOVE <dir>/.file → 目标（服务端把分片拼起来）
                if (key.EndsWith("/.file", StringComparison.Ordinal))
                {
                    var directory = key[..^"/.file".Length];
                    var chunkKeys = _files.Keys
                        .Where(k => k.StartsWith(directory + "/", StringComparison.Ordinal) && !k.EndsWith("/.file", StringComparison.Ordinal))
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .ToList();

                    if (chunkKeys.Count == 0)
                    {
                        return (404, headers, Array.Empty<byte>());
                    }

                    var merged = new List<byte>();
                    foreach (var chunkKey in chunkKeys)
                    {
                        merged.AddRange(_files[chunkKey]);
                        _files.TryRemove(chunkKey, out _);
                    }

                    var assembled = merged.ToArray();
                    _files[destinationKey] = assembled;
                    _etags[destinationKey] = ComputeEtag(assembled);
                    _collections.TryRemove(directory, out _);
                    return (201, headers, Array.Empty<byte>());
                }

                if (!_files.TryRemove(key, out var moved))
                {
                    return (404, headers, Array.Empty<byte>());
                }

                _files[destinationKey] = moved;
                return (201, headers, Array.Empty<byte>());

            case "PROPFIND":
                return (207, headers, Encoding.UTF8.GetBytes(BuildMultiStatus(key)));

            default:
                return (405, headers, Array.Empty<byte>());
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        List<(string, string)> headers,
        byte[] body,
        string method)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        builder.Append("Connection: close\r\n");

        var hasContentLength = headers.Any(h => h.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        foreach (var (name, value) in headers)
        {
            if (hasContentLength && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        builder.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);

        if (method != "HEAD" && body.Length > 0)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string ComputeEtag(byte[] content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(content);
        return "\"" + Convert.ToHexString(hash)[..16].ToLowerInvariant() + "\"";
    }

    /// <summary>Depth:1 的 multistatus：目录本身 + 直接子项（文件与子目录）。</summary>
    private string BuildMultiStatus(string directoryKey)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version='1.0' encoding='utf-8'?>");
        builder.Append("<D:multistatus xmlns:D='DAV:'>");

        void AppendEntry(string key, bool isCollection)
        {
            builder.Append("<D:response><D:href>")
                .Append("/dav/").Append(Uri.EscapeDataString(key).Replace("%2F", "/"))
                .Append(isCollection ? "/" : string.Empty)
                .Append("</D:href><D:propstat><D:prop><D:resourcetype>")
                .Append(isCollection ? "<D:collection/>" : string.Empty)
                .Append("</D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }

        AppendEntry(directoryKey, isCollection: true);

        var prefix = directoryKey.Length == 0 ? string.Empty : directoryKey + "/";
        foreach (var file in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = file[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('/'))
            {
                continue;
            }

            AppendEntry(file, isCollection: false);
        }

        var childCollections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in _collections.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = collection[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('/'))
            {
                continue;
            }

            childCollections.Add(collection);
        }

        foreach (var child in childCollections)
        {
            AppendEntry(child, isCollection: true);
        }

        builder.Append("</D:multistatus>");
        return builder.ToString();
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        207 => "Multi-Status",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        412 => "Precondition Failed",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    private static string Normalize(string path)
    {
        var decoded = Uri.UnescapeDataString(path).Replace('\\', '/').Trim('/');
        if (decoded.StartsWith("dav/", StringComparison.Ordinal))
        {
            decoded = decoded[4..];
        }

        return decoded;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }

    private sealed class HttpRequest
    {
        public required string Method { get; init; }

        public required string Path { get; init; }

        public required Dictionary<string, string> Headers { get; init; }

        public required byte[] Body { get; init; }

        public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>(4096);
            var single = new byte[1];

            // 读 header 直到 \r\n\r\n
            while (true)
            {
                var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                buffer.Add(single[0]);
                var count = buffer.Count;
                if (count >= 4 &&
                    buffer[count - 4] == (byte)'\r' && buffer[count - 3] == (byte)'\n' &&
                    buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
                {
                    break;
                }

                if (count > 64 * 1024)
                {
                    return null;
                }
            }

            var headerText = Encoding.UTF8.GetString(buffer.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var parts = lines[0].Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            byte[] body = Array.Empty<byte>();

            if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
            }
            else if (headers.TryGetValue("Content-Length", out var lengthText) &&
                     int.TryParse(lengthText, out var length) && length > 0)
            {
                body = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset < length)
                {
                    Array.Resize(ref body, offset);
                }
            }

            return new HttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                Path = parts[1],
                Headers = headers,
                Body = body,
            };
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
        {
            using var result = new MemoryStream();
            var lineBuffer = new List<byte>();

            while (true)
            {
                lineBuffer.Clear();
                while (true)
                {
                    var single = new byte[1];
                    var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return result.ToArray();
                    }

                    if (single[0] == (byte)'\n')
                    {
                        break;
                    }

                    if (single[0] != (byte)'\r')
                    {
                        lineBuffer.Add(single[0]);
                    }
                }

                var line = Encoding.ASCII.GetString(lineBuffer.ToArray());
                var semicolon = line.IndexOf(';');
                if (semicolon >= 0)
                {
                    line = line[..semicolon];
                }

                if (!int.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size == 0)
                {
                    // 吃掉尾部 CRLF
                    var crlf = new byte[2];
                    _ = await stream.ReadAsync(crlf, ct).ConfigureAwait(false);
                    return result.ToArray();
                }

                var chunk = new byte[size];
                var offset = 0;
                while (offset < size)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return result.ToArray();
                    }

                    offset += read;
                }

                result.Write(chunk, 0, size);
            }
        }
    }
}
