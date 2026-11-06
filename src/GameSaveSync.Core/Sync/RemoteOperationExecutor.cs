using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Sync;

/// <summary>outbox 项的远端执行器（对象/清单的 PUT 与 DELETE）。</summary>
public interface IRemoteOperationExecutor
{
    Task ExecuteAsync(OutboxItem item, CancellationToken ct);
}

/// <summary>依赖尚未就绪（例如清单引用的对象还没传完）→ 让出队项延迟重试，而不是判失败。</summary>
public sealed class OutboxNotReadyException : Exception
{
    public OutboxNotReadyException(string message) : base(message)
    {
    }
}

public sealed class RemoteOperationExecutor : IRemoteOperationExecutor
{
    private readonly IRemoteStore _remote;
    private readonly IObjectStore _store;
    private readonly IObjectRepository _objects;
    private readonly ISnapshotRepository _snapshots;
    private readonly ISnapshotManifestStore _manifests;
    private readonly ISettingsRepository _settings;
    private readonly IRemoteIndexService _index;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _layoutGate = new(1, 1);
    private bool _layoutReady;

    public RemoteOperationExecutor(
        IRemoteStore remote,
        IObjectStore store,
        IObjectRepository objects,
        ISnapshotRepository snapshots,
        ISnapshotManifestStore manifests,
        ISettingsRepository settings,
        IRemoteIndexService index,
        IAppLogger logger)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(OutboxItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        switch (item.Op)
        {
            case OutboxOperations.PutObject:
            {
                await EnsureLayoutAsync(ct).ConfigureAwait(false);
                var payload = OutboxPayloads.ParseObject(item.Payload);
                if (!_store.Contains(payload.Hash))
                {
                    throw new FileNotFoundException($"本地对象缺失，无法上传：{payload.Hash}");
                }

                await using var stream = _store.OpenReadAsync(payload.Hash, ct);
                await _remote.PutObjectAsync(payload.Hash, stream, ct).ConfigureAwait(false);
                await _objects.MarkUploadedAsync(payload.Hash, ct).ConfigureAwait(false);
                break;
            }

            case OutboxOperations.PutManifest:
            {
                await EnsureLayoutAsync(ct).ConfigureAwait(false);
                var payload = OutboxPayloads.ParseManifest(item.Payload);
                var snapshot = await _snapshots.GetAsync(payload.SnapshotId, ct).ConfigureAwait(false);
                if (snapshot is null)
                {
                    _logger.Warn($"清单对应的快照已不存在，跳过上传：{payload.SnapshotId:N}");
                    return;
                }

                await EnsureObjectsUploadedAsync(payload.SnapshotId, ct).ConfigureAwait(false);

                var manifest = await _manifests.LoadAsync(snapshot, ct).ConfigureAwait(false);
                await _remote.PutManifestAsync(snapshot.GameId, manifest, ct).ConfigureAwait(false);
                await _snapshots
                    .SetRemoteStateAsync(payload.SnapshotId, Models.RemoteSnapshotState.Uploaded, null, ct)
                    .ConfigureAwait(false);
                break;
            }

            case OutboxOperations.PutIndex:
            {
                var payload = OutboxPayloads.ParseManifest(item.Payload);
                var snapshot = await _snapshots.GetAsync(payload.SnapshotId, ct).ConfigureAwait(false);
                if (snapshot is null)
                {
                    _logger.Warn($"索引对应的快照已不存在，跳过发布：{payload.SnapshotId:N}");
                    return;
                }

                // 上行顺序：清单必须已经在远端，索引才能指向它
                if (snapshot.RemoteState != Models.RemoteSnapshotState.Uploaded)
                {
                    throw new OutboxNotReadyException("清单尚未上传完成，索引延后发布");
                }

                var result = await _index.PublishAsync(payload.GameId, payload.SnapshotId, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Error ?? "发布索引失败");
                }

                break;
            }

            case OutboxOperations.DeleteObject:
            {
                var payload = OutboxPayloads.ParseObject(item.Payload);
                await _remote.DeleteObjectAsync(payload.Hash, ct).ConfigureAwait(false);
                break;
            }

            case OutboxOperations.DeleteManifest:
            {
                var payload = OutboxPayloads.ParseManifest(item.Payload);
                await _remote.DeleteManifestAsync(payload.GameId, payload.FileName, ct).ConfigureAwait(false);
                break;
            }

            default:
                throw new InvalidOperationException($"未知的 outbox 操作：{item.Op}");
        }
    }

    /// <summary>首次上传前建远端骨架（目录 + vault.json）；失败留给下一轮重试。</summary>
    private async Task EnsureLayoutAsync(CancellationToken ct)
    {
        if (_layoutReady)
        {
            return;
        }

        await _layoutGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_layoutReady)
            {
                return;
            }

            var deviceId = await _settings.GetDeviceIdAsync(ct).ConfigureAwait(false);
            await _remote.EnsureLayoutAsync(deviceId, ct).ConfigureAwait(false);
            _layoutReady = true;
        }
        finally
        {
            _layoutGate.Release();
        }
    }

    /// <summary>上行顺序约束：清单引用的对象必须全部上传完成，否则本项延迟重试。</summary>
    private async Task EnsureObjectsUploadedAsync(Guid snapshotId, CancellationToken ct)
    {
        var files = await _snapshots.GetFilesAsync(snapshotId, ct).ConfigureAwait(false);
        var hashes = files.Select(f => f.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pending = new List<string>();

        foreach (var hash in hashes)
        {
            var stored = await _objects.GetAsync(hash, ct).ConfigureAwait(false);
            if (stored is null || !stored.Uploaded)
            {
                pending.Add(hash);
            }
        }

        if (pending.Count > 0)
        {
            throw new OutboxNotReadyException($"{pending.Count} 个对象尚未上传完成，清单延后上传");
        }
    }
}
