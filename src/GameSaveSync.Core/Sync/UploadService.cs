using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Sync;

public sealed record UploadOutcome(
    Guid SnapshotId,
    bool Success,
    int ObjectsUploaded,
    int ObjectsSkipped,
    string? Error)
{
    public string Describe() => Success
        ? $"上传成功（新对象 {ObjectsUploaded}，复用 {ObjectsSkipped}）"
        : $"上传失败：{Error}";
}

public interface IUploadService
{
    /// <summary>
    /// 把一个快照的所有网络操作写进 outbox 并立即尝试执行。
    /// 网络失败只影响远端状态（pending），本地快照永远保留。
    /// </summary>
    Task<UploadOutcome> UploadSnapshotAsync(Guid snapshotId, CancellationToken ct);

    /// <summary>重试所有 pending / failed 项（"重试上传"按钮，M2 走 outbox）。</summary>
    Task<IReadOnlyList<UploadOutcome>> UploadPendingAsync(CancellationToken ct);
}

/// <summary>
/// M2 的上传入口：不再直接发网络请求，而是"先把意图写进 outbox，再让 outbox 处理器执行"。
/// 这样进程在任意点被杀，待办都不会丢；对象按 hash 命名，重复执行天然幂等。
/// </summary>
public sealed class UploadService : IUploadService
{
    private readonly ISnapshotRepository _snapshots;
    private readonly IObjectRepository _objects;
    private readonly IObjectStore _store;
    private readonly IRemoteStore _remote;
    private readonly IOutboxStore _outbox;
    private readonly OutboxProcessor _processor;
    private readonly IAppLogger _logger;

    public UploadService(
        ISnapshotRepository snapshots,
        IObjectRepository objects,
        IObjectStore store,
        IRemoteStore remote,
        IOutboxStore outbox,
        OutboxProcessor processor,
        IAppLogger logger)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UploadOutcome> UploadSnapshotAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new UploadOutcome(snapshotId, false, 0, 0, "快照不存在");
        }

        if (snapshot.RemoteState == RemoteSnapshotState.Uploaded)
        {
            return new UploadOutcome(snapshotId, true, 0, 0, null);
        }

        if (!_remote.IsConfigured)
        {
            const string notConfigured = "尚未配置 WebDAV，快照只保存在本地";
            await _snapshots.SetRemoteStateAsync(snapshotId, RemoteSnapshotState.Pending, notConfigured, ct)
                .ConfigureAwait(false);
            _logger.Warn(notConfigured);
            return new UploadOutcome(snapshotId, false, 0, 0, notConfigured);
        }

        var files = await _snapshots.GetFilesAsync(snapshotId, ct).ConfigureAwait(false);
        var hashes = files.Select(f => f.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var enqueued = 0;
        var skipped = 0;

        // 用户这次操作就是"现在就传"：对已排队的项提前重试时间（不重置失败计数）
        const bool forceDue = true;

        foreach (var hash in hashes)
        {
            var stored = await _objects.GetAsync(hash, ct).ConfigureAwait(false);
            if (stored is not null && stored.Uploaded)
            {
                skipped++;
                continue;
            }

            if (!_store.Contains(hash))
            {
                var missing = $"本地对象缺失：{hash}";
                await _snapshots.SetRemoteStateAsync(snapshotId, RemoteSnapshotState.Pending, missing, ct)
                    .ConfigureAwait(false);
                return new UploadOutcome(snapshotId, false, enqueued, skipped, missing);
            }

            var isNew = await _outbox
                .EnqueueAsync(
                    OutboxOperations.PutObject,
                    OutboxPayloads.ForObject(hash),
                    $"obj:{hash}",
                    forceDue,
                    ct)
                .ConfigureAwait(false);

            if (isNew)
            {
                enqueued++;
            }
            else
            {
                skipped++;
            }
        }

        var fileName = SnapshotNames.ManifestFileName(snapshot);
        await _outbox.EnqueueAsync(
            OutboxOperations.PutManifest,
            OutboxPayloads.ForManifest(snapshot.GameId, snapshotId, fileName),
            $"man:{snapshot.GameId:D}:{fileName}",
            forceDue,
            ct).ConfigureAwait(false);

        // 上行顺序的最后一环：对象 → 清单 → index
        await _outbox.EnqueueAsync(
            OutboxOperations.PutIndex,
            OutboxPayloads.ForManifest(snapshot.GameId, snapshotId, fileName),
            $"idx:{snapshot.GameId:D}:{snapshotId:N}",
            forceDue,
            ct).ConfigureAwait(false);

        await _snapshots.SetRemoteStateAsync(snapshotId, RemoteSnapshotState.Pending, null, ct)
            .ConfigureAwait(false);

        await _processor.ProcessDueAsync(ct).ConfigureAwait(false);

        var reloaded = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false);
        if (reloaded is not null && reloaded.RemoteState == RemoteSnapshotState.Uploaded)
        {
            _logger.Info($"快照 {fileName} 上传完成（新对象 {enqueued}，复用 {skipped}）");
            return new UploadOutcome(snapshotId, true, enqueued, skipped, null);
        }

        var pendingLeft = await _outbox.CountPendingAsync(ct).ConfigureAwait(false);
        var error = await _outbox.GetLastErrorAsync(ct).ConfigureAwait(false)
                    ?? reloaded?.LastError
                    ?? $"仍有 {pendingLeft} 项待上传（网络或服务端问题）";

        // 把失败原因落回快照，UI 直接能看到"为什么没传上去"
        await _snapshots.SetRemoteStateAsync(snapshotId, RemoteSnapshotState.Pending, error, ct)
            .ConfigureAwait(false);

        return new UploadOutcome(snapshotId, false, enqueued, skipped, error);
    }

    public async Task<IReadOnlyList<UploadOutcome>> UploadPendingAsync(CancellationToken ct)
    {
        // 用户主动点了重试：把 failed 的项也放回队列
        await _outbox.ResetFailedAsync(ct).ConfigureAwait(false);

        var targets = await _snapshots.ListPendingAsync(ct).ConfigureAwait(false);

        foreach (var snapshot in targets)
        {
            var files = await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false);
            foreach (var hash in files.Select(f => f.Hash).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var stored = await _objects.GetAsync(hash, ct).ConfigureAwait(false);
                if (stored is null || !stored.Uploaded)
                {
                    await _outbox.EnqueueAsync(
                        OutboxOperations.PutObject,
                        OutboxPayloads.ForObject(hash),
                        $"obj:{hash}",
                        forceDue: true,
                        ct).ConfigureAwait(false);
                }
            }

            var fileName = SnapshotNames.ManifestFileName(snapshot);
            await _outbox.EnqueueAsync(
                OutboxOperations.PutManifest,
                OutboxPayloads.ForManifest(snapshot.GameId, snapshot.Id, fileName),
                $"man:{snapshot.GameId:D}:{fileName}",
                forceDue: true,
                ct).ConfigureAwait(false);

            // 索引也要一起补：断网时失败的 put_index 可能还压在退避里
            await _outbox.EnqueueAsync(
                OutboxOperations.PutIndex,
                OutboxPayloads.ForManifest(snapshot.GameId, snapshot.Id, fileName),
                $"idx:{snapshot.GameId:D}:{snapshot.Id:N}",
                forceDue: true,
                ct).ConfigureAwait(false);
        }

        await _processor.ProcessDueAsync(ct).ConfigureAwait(false);

        var results = new List<UploadOutcome>();
        foreach (var snapshot in targets)
        {
            var reloaded = await _snapshots.GetAsync(snapshot.Id, ct).ConfigureAwait(false);
            var success = reloaded?.RemoteState == RemoteSnapshotState.Uploaded;
            results.Add(new UploadOutcome(
                snapshot.Id,
                success,
                ObjectsUploaded: 0,
                ObjectsSkipped: 0,
                Error: success ? null : reloaded?.LastError ?? "仍待上传"));
        }

        return results;
    }
}
