using System.Net;

namespace GameSaveSync.WebDav.Providers;

/// <summary>
/// 服务商适配层：不同 WebDAV 实现的上传差异都收敛在这里。
/// <list type="bullet">
/// <item>Generic：单次 PUT（失败由上层 outbox 重试）。</item>
/// <item>Nextcloud：大文件走 chunked upload（MKCOL + 分片 PUT + MOVE）。</item>
/// <item>RateLimited：把调用串行化并限速（坚果云这类有频率限制的服务）。</item>
/// </list>
/// 已知坑的统一处理：不支持 MOVE → 退化为直接 PUT；MKCOL 不递归 → 逐级创建；
/// 不返回 ETag → 用内容哈希兜底；HEAD 不支持 → GET Range 探测（见 WebDavClient）。
/// </summary>
public interface IProviderAdapter
{
    string Name { get; }

    Task PutObjectAsync(WebDavClient client, string path, Stream content, long? length, CancellationToken ct);
}

public sealed class GenericProviderAdapter : IProviderAdapter
{
    public static GenericProviderAdapter Instance { get; } = new();

    public string Name => "generic";

    public async Task PutObjectAsync(WebDavClient client, string path, Stream content, long? length, CancellationToken ct)
    {
        var response = await client.PutStreamAsync(path, content, ct).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new Internal.WebDavException($"上传失败：{path}（{(int)response.StatusCode}）", response.StatusCode);
        }
    }
}

/// <summary>
/// Nextcloud/ownCloud 的分块上传：先建 <c>&lt;path&gt;-chunking-&lt;id&gt;</c> 目录，
/// 逐片 PUT，最后 MOVE 目录里的 <c>.file</c> 到目标路径。
/// 任一步不支持（405/501）就退化为单次 PUT —— 结果一致，只是更费带宽。
/// </summary>
public sealed class NextcloudChunkedAdapter : IProviderAdapter
{
    private readonly int _chunkSize;
    private readonly Func<long?, bool> _shouldChunk;

    public NextcloudChunkedAdapter(int chunkSizeBytes = 8 * 1024 * 1024, Func<long?, bool>? shouldChunk = null)
    {
        _chunkSize = Math.Max(256 * 1024, chunkSizeBytes);
        _shouldChunk = shouldChunk ?? (length => length is null || length > _chunkSize);
    }

    public string Name => "nextcloud-chunked";

    public async Task PutObjectAsync(WebDavClient client, string path, Stream content, long? length, CancellationToken ct)
    {
        if (!_shouldChunk(length))
        {
            await GenericProviderAdapter.Instance.PutObjectAsync(client, path, content, length, ct).ConfigureAwait(false);
            return;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var directory = $"{path}-chunking-{transferId}";
        var startPosition = content.CanSeek ? content.Position : -1;

        try
        {
            await client.EnsureCollectionAsync(directory, ct).ConfigureAwait(false);

            var buffer = new byte[_chunkSize];
            var index = 0;
            while (true)
            {
                var read = await ReadFullAsync(content, buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                index++;
                var chunkPath = $"{directory}/{index:D5}";
                using var chunk = new MemoryStream(buffer, 0, read, writable: false);
                var response = await client.PutStreamAsync(chunkPath, chunk, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    throw new Internal.WebDavException(
                        $"分片上传失败：{chunkPath}（{(int)response.StatusCode}）", response.StatusCode);
                }
            }

            if (index == 0)
            {
                // 空文件：一次 PUT 即可
                await client.PutBytesAsync(path, Array.Empty<byte>(), ct).ConfigureAwait(false);
                return;
            }

            var moved = await client.TryMoveAsync($"{directory}/.file", path, ct).ConfigureAwait(false);
            if (!moved)
            {
                throw new Internal.WebDavException("服务端不支持分块上传的 MOVE 收尾", HttpStatusCode.MethodNotAllowed);
            }
        }
        catch (Exception ex) when (ex is Internal.WebDavException or NotSupportedException)
        {
            // 服务端不支持分块：能回退就回退成单次 PUT（CAS 的流是 FileStream，可 Seek）
            if (startPosition < 0)
            {
                throw;
            }

            content.Position = startPosition;
            await GenericProviderAdapter.Instance.PutObjectAsync(client, path, content, length, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }
}

/// <summary>串行 + 限速的装饰器（真正的限速在 <see cref="WebDavOptions.RateLimited"/> 里生效）。</summary>
public sealed class RateLimitedProviderAdapter : IProviderAdapter
{
    private readonly IProviderAdapter _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public RateLimitedProviderAdapter(IProviderAdapter inner, TimeSpan? minInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(350);
    }

    public string Name => $"rate-limited({_inner.Name})";

    public async Task PutObjectAsync(WebDavClient client, string path, Stream content, long? length, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCall;
            var wait = _minInterval - elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            await _inner.PutObjectAsync(client, path, content, length, ct).ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class ProviderAdapterFactory
{
    public static IProviderAdapter Create(WebDavOptions options)
    {
        IProviderAdapter adapter = options.Provider switch
        {
            WebDavProvider.Nextcloud => new NextcloudChunkedAdapter(options.ChunkSizeBytes),
            _ => GenericProviderAdapter.Instance,
        };

        return options.RateLimited ? new RateLimitedProviderAdapter(adapter, TimeSpan.FromMilliseconds(options.MinRequestIntervalMs)) : adapter;
    }
}
