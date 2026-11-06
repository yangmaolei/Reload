using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;

namespace GameSaveSync.Core.Sync;

/// <summary>
/// 远端存储契约（签名冻结）。M1 只实现 WebDAV；M3 在此之上加 IProviderAdapter。
/// 上传顺序固定：对象先、清单后（保证清单出现时引用已就位）。
/// </summary>
public interface IRemoteStore
{
    /// <summary>是否已配置（URL/凭据齐全）。未配置时上传直接标记 pending，不做无意义重试。</summary>
    bool IsConfigured { get; }

    /// <summary>内容寻址对象，不可变、重复 PUT 幂等。</summary>
    Task PutObjectAsync(string hash, Stream content, CancellationToken ct);

    Task PutManifestAsync(Guid gameId, SnapshotManifest manifest, CancellationToken ct);

    Task<Stream?> GetObjectAsync(string hash, CancellationToken ct);

    /// <summary>M3 的下行同步用；M1 只做上传。</summary>
    Task<string?> GetManifestJsonAsync(Guid gameId, string fileName, CancellationToken ct);

    /// <summary>M2 保留策略会删远端对象/清单；失败不影响本地清理，由 outbox 重试。</summary>
    Task DeleteObjectAsync(string hash, CancellationToken ct);

    Task DeleteManifestAsync(Guid gameId, string fileName, CancellationToken ct);

    /// <summary>MKCOL 建目录 + 写探针文件再删除。</summary>
    Task TestConnectionAsync(CancellationToken ct);

    /// <summary>建远端骨架（目录 + vault.json），幂等。首次上传前调用。</summary>
    Task EnsureLayoutAsync(string deviceId, CancellationToken ct);

    // ===== M3：全局索引（唯一需要并发控制的文件） =====

    /// <summary>读 index.json；带 If-None-Match 时命中 304 返回 <see cref="RemoteIndexFetch.NotModified"/>。</summary>
    Task<RemoteIndexFetch> GetIndexAsync(string? ifNoneMatch, CancellationToken ct);

    /// <summary>写 index.json（临时名 + MOVE），返回 ETag 或 412 冲突。</summary>
    Task<RemoteIndexWrite> PutIndexAsync(string json, string? ifMatch, string? ifNoneMatch, CancellationToken ct);

    /// <summary>列目录（PROPFIND Depth: 1）。仅用于"索引损坏后重建"，绝不逐文件 PROPFIND。</summary>
    Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct);

    Task PutDeviceAsync(DeviceDocument device, CancellationToken ct);

    Task<IReadOnlyList<DeviceDocument>> ListDevicesAsync(CancellationToken ct);
}

/// <summary>读索引的结果：<c>NotModified</c> 为 true 时其余字段无意义（304）。</summary>
public sealed record RemoteIndexFetch(string? Json, string? ETag, bool NotModified)
{
    public bool Exists => Json is not null;
}

/// <summary>写索引的结果：412 表示 ETag 已变，调用方要重新读取并合并。</summary>
public sealed record RemoteIndexWrite(bool Success, bool PreconditionFailed, string? ETag, string? Error);

public sealed class RemoteNotConfiguredException : Exception
{
    public RemoteNotConfiguredException() : base("尚未配置 WebDAV 远端")
    {
    }
}

/// <summary>未配置远端的空实现：所有写操作抛 <see cref="RemoteNotConfiguredException"/>。</summary>
public sealed class NullRemoteStore : IRemoteStore
{
    public static NullRemoteStore Instance { get; } = new();

    public bool IsConfigured => false;

    public Task PutObjectAsync(string hash, Stream content, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task PutManifestAsync(Guid gameId, SnapshotManifest manifest, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task<Stream?> GetObjectAsync(string hash, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task<string?> GetManifestJsonAsync(Guid gameId, string fileName, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task DeleteObjectAsync(string hash, CancellationToken ct) => throw new RemoteNotConfiguredException();

    public Task DeleteManifestAsync(Guid gameId, string fileName, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task TestConnectionAsync(CancellationToken ct) => throw new RemoteNotConfiguredException();

    public Task EnsureLayoutAsync(string deviceId, CancellationToken ct) => throw new RemoteNotConfiguredException();

    public Task<RemoteIndexFetch> GetIndexAsync(string? ifNoneMatch, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task<RemoteIndexWrite> PutIndexAsync(
        string json, string? ifMatch, string? ifNoneMatch, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task PutDeviceAsync(DeviceDocument device, CancellationToken ct) =>
        throw new RemoteNotConfiguredException();

    public Task<IReadOnlyList<DeviceDocument>> ListDevicesAsync(CancellationToken ct) =>
        throw new RemoteNotConfiguredException();
}
