using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.WebDav.Internal;
using GameSaveSync.WebDav.Providers;

namespace GameSaveSync.WebDav;

public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>
/// WebDAV 远端存储。远端布局严格按规格：
/// <code>
/// /GameSaveSync/vault.json
/// /GameSaveSync/index.json                       （M3）
/// /GameSaveSync/games/&lt;gameId&gt;/snapshots/&lt;ts&gt;-&lt;sid&gt;.json
/// /GameSaveSync/objects/&lt;hh&gt;/&lt;hash&gt;
/// </code>
/// objects 不可变、重复 PUT 幂等；清单先写临时名再 MOVE。
/// </summary>
public sealed class WebDavRemoteStore : IRemoteStore, IDisposable
{
    private readonly WebDavOptions _options;
    private readonly WebDavClient _client;
    private readonly IAppLogger _logger;
    private readonly IProviderAdapter _adapter;

    public WebDavRemoteStore(WebDavOptions options, IAppLogger? logger = null)
        : this(options, logger, null)
    {
    }

    public WebDavRemoteStore(WebDavOptions options, IAppLogger? logger, WebDavClient? client)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? new InMemoryAppLogger();
        _client = client ?? new WebDavClient(options);
        _adapter = ProviderAdapterFactory.Create(options);
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>上一次读到的 index ETag 是否由服务端提供（决定还能不能用 If-Match）。</summary>
    private bool _indexEtagFromServer;

    /// <summary>服务端不返回 ETag 时的兜底：用内容哈希做"弱 ETag"，仅用于本地比较。</summary>
    private string? _lastIndexContentHash;

    public static string ObjectPath(string hash) => $"objects/{hash[..2].ToLowerInvariant()}/{hash.ToLowerInvariant()}";

    public static string ManifestPath(Guid gameId, string fileName) =>
        $"games/{gameId:D}/snapshots/{fileName}";

    public async Task EnsureVaultAsync(string deviceId, CancellationToken ct)
    {
        await _client.EnsureCollectionAsync("objects", ct).ConfigureAwait(false);
        await _client.EnsureCollectionAsync("games", ct).ConfigureAwait(false);

        if (await _client.ExistsAsync("vault.json", ct).ConfigureAwait(false))
        {
            return;
        }

        var vault = new
        {
            version = 1,
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            createdByDevice = deviceId,
            encrypted = false,
            kdf = (object?)null,
        };

        var json = JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true });
        await _client.PutAsync("vault.json", Encoding.UTF8.GetBytes(json), ct, ifNoneMatch: "*")
            .ConfigureAwait(false);
    }

    public Task EnsureLayoutAsync(string deviceId, CancellationToken ct) => EnsureVaultAsync(deviceId, ct);

    public async Task PutObjectAsync(string hash, Stream content, CancellationToken ct)
    {
        var path = ObjectPath(hash);
        var bucket = path[..path.LastIndexOf('/')];

        // 已存在就跳过（对象内容寻址，重复 PUT 也是幂等，只是省流量）
        if (await _client.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return;
        }

        await _client.EnsureCollectionAsync(bucket, ct).ConfigureAwait(false);
        var length = content.CanSeek ? content.Length - content.Position : (long?)null;
        await _adapter.PutObjectAsync(_client, path, content, length, ct).ConfigureAwait(false);
    }

    public async Task PutManifestAsync(Guid gameId, SnapshotManifest manifest, CancellationToken ct)
    {
        var fileName = SnapshotNames.ManifestFileName(manifest.CreatedAt, manifest.SnapshotId);
        var path = ManifestPath(gameId, fileName);
        var json = SnapshotManifestSerializer.ToJson(manifest);
        await _client.PutWithTempNameAsync(path, Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        _logger.Info($"清单已上传：{path}");
    }

    public async Task<Stream?> GetObjectAsync(string hash, CancellationToken ct)
    {
        var bytes = await _client.GetAsync(ObjectPath(hash), ct).ConfigureAwait(false);
        return bytes is null ? null : new MemoryStream(bytes, writable: false);
    }

    public async Task<string?> GetManifestJsonAsync(Guid gameId, string fileName, CancellationToken ct)
    {
        var bytes = await _client.GetAsync(ManifestPath(gameId, fileName), ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async Task<RemoteIndexFetch> GetIndexAsync(string? ifNoneMatch, CancellationToken ct)
    {
        var headers = string.IsNullOrEmpty(ifNoneMatch)
            ? Array.Empty<(string, string)>()
            : new[] { ("If-None-Match", ifNoneMatch) };

        var response = await _client.SendAsync(HttpMethod.Get, "index.json", null, ct, headers)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new RemoteIndexFetch(null, ifNoneMatch, NotModified: true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _indexEtagFromServer = false;
            _lastIndexContentHash = null;
            return new RemoteIndexFetch(null, null, NotModified: false);
        }

        WebDavClient.ThrowIfAuthenticationFailed(response);
        if (!response.IsSuccess)
        {
            throw new WebDavException($"读取 index.json 失败（{(int)response.StatusCode}）", response.StatusCode);
        }

        var json = Encoding.UTF8.GetString(response.Content);
        _lastIndexContentHash = Hash(json);
        _indexEtagFromServer = response.ETag is not null;

        // 服务端不给 ETag 时用内容哈希兜底（只在本机比较，不发给服务端做 If-Match）
        return new RemoteIndexFetch(json, response.ETag ?? $"\"{_lastIndexContentHash}\"", NotModified: false);
    }

    public async Task<RemoteIndexWrite> PutIndexAsync(
        string json,
        string? ifMatch,
        string? ifNoneMatch,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        // 服务端不提供 ETag 时 If-Match 永远匹配不上：退化为无条件 PUT。
        // 但无条件 PUT 会丢更新（两台设备先后发布，后写的覆盖先写的条目）。
        // 降级方案：写入前重新读取远端索引，比对内容哈希——
        // 远端内容与我们读到的不一致，说明期间有别的设备写过 → 报 412 让上层重读重合并。
        var canUseIfMatch = _indexEtagFromServer && !string.IsNullOrEmpty(ifMatch);
        if (!canUseIfMatch && _lastIndexContentHash is not null)
        {
            var current = await GetIndexAsync(null, ct).ConfigureAwait(false);
            if (current.NotModified)
            {
                // 读到的还是自己那份，没被别人改过，可以写
            }
            else if (current.Json is not null)
            {
                var currentHash = Hash(current.Json);
                if (!string.Equals(currentHash, _lastIndexContentHash, StringComparison.Ordinal))
                {
                    return new RemoteIndexWrite(false, PreconditionFailed: true, ETag: null,
                        Error: "索引已被其他设备修改（服务端不支持 ETag，改用内容比对）");
                }
            }
        }

        var response = await _client
            .PutBytesAsync("index.json", bytes, ct, canUseIfMatch ? ifMatch : null, ifNoneMatch)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestEntityTooLarge
            or (HttpStatusCode)428)
        {
            return new RemoteIndexWrite(false, PreconditionFailed: true, ETag: null,
                Error: "索引已被其他设备修改（412）");
        }

        if (!response.IsSuccess)
        {
            return new RemoteIndexWrite(false, false, null, $"写入 index.json 失败（{(int)response.StatusCode}）");
        }

        _lastIndexContentHash = Hash(json);
        if (response.ETag is not null)
        {
            _indexEtagFromServer = true;
            return new RemoteIndexWrite(true, false, response.ETag, null);
        }

        return new RemoteIndexWrite(true, false, $"\"{_lastIndexContentHash}\"", null);
    }

    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct) =>
        _client.ListAsync(relativeDirectory, ct);

    public Task PutDeviceAsync(DeviceDocument device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        var json = IndexSerializer.ToJson(device);
        return _client.PutWithTempNameAsync($"devices/{device.Id}.json", Encoding.UTF8.GetBytes(json), ct);
    }

    public async Task<IReadOnlyList<DeviceDocument>> ListDevicesAsync(CancellationToken ct)
    {
        var names = await _client.ListAsync("devices", ct).ConfigureAwait(false);
        var result = new List<DeviceDocument>();

        foreach (var name in names.Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var json = await _client.GetAsync($"devices/{name}", ct).ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            var device = IndexSerializer.TryParseDevice(Encoding.UTF8.GetString(json));
            if (device is not null)
            {
                result.Add(device);
            }
        }

        return result;
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    public Task DeleteObjectAsync(string hash, CancellationToken ct) =>
        _client.DeleteAsync(ObjectPath(hash), ct);

    public Task DeleteManifestAsync(Guid gameId, string fileName, CancellationToken ct) =>
        _client.DeleteAsync(ManifestPath(gameId, fileName), ct);

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        var result = await TestConnectionDetailedAsync(ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new WebDavException(result.Message, System.Net.HttpStatusCode.BadRequest);
        }
    }

    /// <summary>MKCOL 建目录 → PUT 探针 → GET 校验 → DELETE 清理。</summary>
    public async Task<ConnectionTestResult> TestConnectionDetailedAsync(CancellationToken ct)
    {
        try
        {
            await _client.EnsureCollectionAsync(string.Empty, ct).ConfigureAwait(false);
            await _client.EnsureCollectionAsync(".probe", ct).ConfigureAwait(false);

            var probePath = $".probe/{Guid.NewGuid():N}.txt";
            var payload = Encoding.UTF8.GetBytes($"GameSaveSync probe {DateTimeOffset.UtcNow:O}");
            await _client.PutAsync(probePath, payload, ct).ConfigureAwait(false);

            var readBack = await _client.GetAsync(probePath, ct).ConfigureAwait(false);
            if (readBack is null || readBack.Length != payload.Length)
            {
                return new ConnectionTestResult(false, "写入成功但读回内容不一致，请检查服务端配置");
            }

            await _client.DeleteAsync(probePath, ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, "连接成功：目录创建、写入、读取、删除均已验证");
        }
        catch (WebDavAuthenticationException ex)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WebDavException)
        {
            return new ConnectionTestResult(false, $"连接失败：{ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();
}
