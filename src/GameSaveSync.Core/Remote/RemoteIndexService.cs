using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.Core.Remote;

public sealed record IndexPublishResult(
    bool Success,
    bool Conflict,
    string? Error,
    IndexDocument? Index,
    IReadOnlyList<IndexConflict> Conflicts);

public sealed record IndexPullResult(
    bool Changed,
    int ImportedSnapshots,
    IReadOnlyList<IndexConflict> Conflicts,
    bool Rebuilt,
    string? Error,
    int FailedImports = 0);

public interface IRemoteIndexService
{
    /// <summary>把"本机这个快照"发布进 index.json（乐观锁 + 412 合并重试）。</summary>
    Task<IndexPublishResult> PublishAsync(Guid gameId, Guid snapshotId, CancellationToken ct);

    /// <summary>下行：拉 index.json（304 直接结束），把本机没见过的快照落地。</summary>
    Task<IndexPullResult> PullAsync(CancellationToken ct);

    /// <summary>用户在冲突界面做出选择后调用：直接覆盖该游戏的索引条目并清除 conflict。</summary>
    Task<bool> ResolveConflictAsync(Guid gameId, Guid snapshotId, CancellationToken ct);

    /// <summary>索引损坏时扫 games/*/snapshots/* 重建（方案里明确允许的唯一全量列举）。</summary>
    Task<IndexDocument> RebuildAsync(CancellationToken ct);

    Task<IReadOnlyList<DeviceDocument>> RefreshDevicesAsync(CancellationToken ct);
}

/// <summary>
/// index.json 的读写中枢。
/// 上行：对象 → 清单 → index（前两步在 outbox 里，本服务只负责最后一步）。
/// 下行：只轮询这一个文件，命中 304 就结束；发现未知 snapshotId 才拉清单与对象。
/// </summary>
public sealed class RemoteIndexService : IRemoteIndexService
{
    private const int MaxPublishAttempts = 5;

    private readonly IRemoteStore _remote;
    private readonly IDeviceRepository _devices;
    private readonly IIndexStateRepository _state;
    private readonly ISnapshotRepository _snapshots;
    private readonly IGameRepository _games;
    private readonly IRemoteSnapshotImporter _importer;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    /// <summary>对外报告的应用版本（随程序集版本走，避免硬编码过期）。</summary>
    private static readonly string AppVersion =
        typeof(RemoteIndexService).Assembly.GetName().Version?.ToString() ?? "0.4.0";

    private string? _indexETag;
    private IndexDocument? _cachedIndex;

    public RemoteIndexService(
        IRemoteStore remote,
        IDeviceRepository devices,
        IIndexStateRepository state,
        ISnapshotRepository snapshots,
        IGameRepository games,
        IRemoteSnapshotImporter importer,
        IClock clock,
        IAppLogger logger)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>测试与诊断用：累计收到的 304 次数。</summary>
    public int NotModifiedCount { get; private set; }

    public int PullCount { get; private set; }

    public async Task<IndexPublishResult> PublishAsync(Guid gameId, Guid snapshotId, CancellationToken ct)
    {
        if (!_remote.IsConfigured)
        {
            return new IndexPublishResult(false, false, "尚未配置 WebDAV", null, Array.Empty<IndexConflict>());
        }

        var device = await _devices.GetLocalAsync(ct).ConfigureAwait(false);
        var gameKey = gameId.ToString("D");
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxPublishAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            IndexDocument remote;
            string? etag;
            bool remoteExists;

            try
            {
                // 每次尝试都重新读取：412 之后必须基于最新内容重新合并
                var fetch = await _remote.GetIndexAsync(null, ct).ConfigureAwait(false);
                if (fetch.NotModified)
                {
                    remote = _cachedIndex ?? new IndexDocument();
                    etag = fetch.ETag;
                    remoteExists = true;
                }
                else if (!fetch.Exists)
                {
                    remote = new IndexDocument();
                    etag = null;
                    remoteExists = false;
                }
                else
                {
                    var parsed = IndexSerializer.TryParse(fetch.Json);
                    if (parsed is null)
                    {
                        _logger.Warn("index.json 不是合法 JSON，发布前先重建");
                        remote = await RebuildAsync(ct).ConfigureAwait(false);
                        etag = null;
                        remoteExists = true;
                    }
                    else
                    {
                        remote = parsed;
                        etag = fetch.ETag;
                        remoteExists = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new IndexPublishResult(false, false, $"读取索引失败：{ex.Message}", null,
                    Array.Empty<IndexConflict>());
            }

            // 逻辑时钟：本机序号要"压过"远端看到的那个，否则新设备永远赢不过旧条目
            var remoteSeq = remote.Games.TryGetValue(gameKey, out var remoteEntry) ? remoteEntry.Seq : 0;
            var seq = await _devices.NextSeqAtLeastAsync(remoteSeq, ct).ConfigureAwait(false);
            var entry = new IndexGameEntry
            {
                Seq = seq,
                SnapshotId = snapshotId,
                DeviceId = device.Id,
                UpdatedAt = _clock.UtcNowMs,
                Conflict = false,
            };

            var local = new Dictionary<string, IndexGameEntry>(
                await _state.LoadLocalAsync(ct).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase)
            {
                [gameKey] = entry,
            };

            var graph = await LoadGraphAsync(ct).ConfigureAwait(false);
            var merge = IndexMerger.Merge(new IndexMergeRequest
            {
                Remote = remote,
                Local = local,
                LocalDeviceId = device.Id,
                AreRelated = (a, b) => AreRelated(graph, a, b),
                CreatedAtOf = id => graph.TryGetValue(id, out var node) ? node.CreatedAt : null,
                NowMs = _clock.UtcNowMs,
            });

            RemoteIndexWrite write;
            try
            {
                write = await _remote.PutIndexAsync(
                    IndexSerializer.ToJson(merge.Merged),
                    ifMatch: remoteExists ? etag : null,
                    ifNoneMatch: remoteExists ? null : "*",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new IndexPublishResult(false, false, $"写入索引失败：{ex.Message}", null,
                    Array.Empty<IndexConflict>());
            }

            if (write.Success)
            {
                _indexETag = write.ETag;
                _cachedIndex = merge.Merged;

                await _state.SaveLocalAsync(gameId, entry, ct).ConfigureAwait(false);
                await _state
                    .ReplaceRemoteAsync(
                        merge.Merged.Games.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
                        _clock.UtcNowMs,
                        ct)
                    .ConfigureAwait(false);

                var conflict = merge.Merged.Games.TryGetValue(gameKey, out var mergedEntry) &&
                               mergedEntry.Conflict;

                _logger.Info(
                    $"index.json 已更新（第 {attempt} 次尝试）：{gameId:N} seq={entry.Seq}" +
                    (conflict ? "，检测到冲突（两个快照都保留）" : string.Empty));

                return new IndexPublishResult(true, conflict, null, merge.Merged, merge.Conflicts);
            }

            if (!write.PreconditionFailed)
            {
                return new IndexPublishResult(false, false, write.Error, null, Array.Empty<IndexConflict>());
            }

            lastError = write.Error ?? "索引被其他设备修改";
            _indexETag = null;
            _cachedIndex = null;

            // 抖动退避后再读一次最新索引合并
            var delay = 60 * attempt + Random.Shared.Next(0, 80);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        _logger.Warn($"index.json 连续 {MaxPublishAttempts} 次写入冲突，留给 outbox 重试");
        return new IndexPublishResult(false, false, lastError, null, Array.Empty<IndexConflict>());
    }

    public async Task<IndexPullResult> PullAsync(CancellationToken ct)
    {
        if (!_remote.IsConfigured)
        {
            return new IndexPullResult(false, 0, Array.Empty<IndexConflict>(), false, "尚未配置 WebDAV");
        }

        PullCount++;

        RemoteIndexFetch fetch;
        try
        {
            fetch = await _remote.GetIndexAsync(_indexETag, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IndexPullResult(false, 0, Array.Empty<IndexConflict>(), false, ex.Message);
        }

        if (fetch.NotModified)
        {
            NotModifiedCount++;
            return new IndexPullResult(false, 0, Array.Empty<IndexConflict>(), false, null);
        }

        var rebuilt = false;
        IndexDocument? document;
        if (!fetch.Exists)
        {
            document = new IndexDocument();
        }
        else
        {
            document = IndexSerializer.TryParse(fetch.Json);
            if (document is null)
            {
                _logger.Warn("index.json 损坏（非法 JSON），开始重建索引");
                document = await RebuildAsync(ct).ConfigureAwait(false);
                rebuilt = true;
            }
        }

        _indexETag = fetch.ETag;
        _cachedIndex = document;

        await _state
            .ReplaceRemoteAsync(document.Games, _clock.UtcNowMs, ct)
            .ConfigureAwait(false);

        var imported = 0;
        var failedImports = 0;
        foreach (var (gameId, entry) in document.Games)
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParse(gameId, out var id))
            {
                continue;
            }

            if (await _snapshots.GetAsync(entry.SnapshotId, ct).ConfigureAwait(false) is not null)
            {
                continue;
            }

            try
            {
                if (await _importer.ImportAsync(id, entry.SnapshotId, ct).ConfigureAwait(false))
                {
                    imported++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedImports++;
                _logger.Warn($"导入远端快照失败：{gameId} / {entry.SnapshotId:N}", ex);
            }
        }

        var conflicts = document.Games
            .Where(kv => kv.Value.Conflict)
            .Select(kv => new IndexConflict(
                kv.Key,
                new IndexGameEntry(),
                kv.Value.Clone(),
                kv.Value.Clone()))
            .ToList();

        if (imported > 0 || rebuilt)
        {
            _logger.Info($"下行同步：导入 {imported} 个远端快照" + (rebuilt ? "（索引已重建）" : string.Empty));
        }

        if (failedImports > 0)
        {
            // 有快照没落地：丢掉缓存的 ETag，让下一次轮询重新读一遍并重试导入，
            // 否则 304 会让失败的快照永远补不上（C5 验收踩到的坑）。
            _indexETag = null;
            _logger.Warn($"本次有 {failedImports} 个远端快照导入失败，下次轮询将重试");
        }

        return new IndexPullResult(true, imported, conflicts, rebuilt, null, failedImports);
    }

    public async Task<bool> ResolveConflictAsync(Guid gameId, Guid snapshotId, CancellationToken ct)
    {
        if (!_remote.IsConfigured)
        {
            return false;
        }

        var device = await _devices.GetLocalAsync(ct).ConfigureAwait(false);

        for (var attempt = 1; attempt <= MaxPublishAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var fetch = await _remote.GetIndexAsync(null, ct).ConfigureAwait(false);
            var document = !fetch.Exists
                ? new IndexDocument()
                : IndexSerializer.TryParse(fetch.Json) ?? await RebuildAsync(ct).ConfigureAwait(false);

            var key = gameId.ToString("D");
            var remoteSeq = document.Games.TryGetValue(key, out var existing) ? existing.Seq : 0;
            var seq = await _devices.NextSeqAtLeastAsync(remoteSeq, ct).ConfigureAwait(false);

            document.Games[key] = new IndexGameEntry
            {
                Seq = seq,
                SnapshotId = snapshotId,
                DeviceId = device.Id,
                UpdatedAt = _clock.UtcNowMs,
                Conflict = false,          // 用户已选择，清除冲突标记
            };
            document.UpdatedAt = _clock.UtcNowMs;

            var write = await _remote.PutIndexAsync(
                IndexSerializer.ToJson(document),
                ifMatch: fetch.Exists ? fetch.ETag : null,
                ifNoneMatch: fetch.Exists ? null : "*",
                ct).ConfigureAwait(false);

            if (write.Success)
            {
                _indexETag = write.ETag;
                _cachedIndex = document;
                await _state.SaveLocalAsync(gameId, document.Games[key], ct).ConfigureAwait(false);
                await _state
                    .ReplaceRemoteAsync(document.Games, _clock.UtcNowMs, ct)
                    .ConfigureAwait(false);

                _logger.Info($"冲突已解决：{gameId:N} 选定 {snapshotId:N}");
                return true;
            }

            if (!write.PreconditionFailed)
            {
                return false;
            }

            await Task.Delay(60 * attempt + Random.Shared.Next(0, 60), ct).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<IndexDocument> RebuildAsync(CancellationToken ct)
    {
        _logger.Warn("开始重建 index.json（扫描 games/*/snapshots/*）");

        var document = new IndexDocument { UpdatedAt = _clock.UtcNowMs };
        var gameIds = await _remote.ListAsync("games", ct).ConfigureAwait(false);

        foreach (var gameIdText in gameIds)
        {
            if (!Guid.TryParse(gameIdText, out var gameId))
            {
                continue;
            }

            IReadOnlyList<string> files;
            try
            {
                files = await _remote.ListAsync($"games/{gameId:D}/snapshots", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            SnapshotManifest? newest = null;
            foreach (var file in files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var json = await _remote.GetManifestJsonAsync(gameId, file, ct).ConfigureAwait(false);
                    if (json is null)
                    {
                        continue;
                    }

                    var manifest = Snapshots.SnapshotManifestSerializer.FromJson(json);
                    if (newest is null || manifest.CreatedAt > newest.CreatedAt)
                    {
                        newest = manifest;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn($"重建索引时跳过损坏清单：{file}", ex);
                }
            }

            if (newest is null)
            {
                continue;
            }

            document.Games[gameId.ToString("D")] = new IndexGameEntry
            {
                Seq = 1,
                SnapshotId = newest.SnapshotId,
                DeviceId = newest.DeviceId,
                UpdatedAt = newest.CreatedAt,
                Conflict = false,
            };
        }

        // 重建结果直接覆盖远端（此时没有可信 ETag）
        try
        {
            var write = await _remote
                .PutIndexAsync(IndexSerializer.ToJson(document), null, null, ct)
                .ConfigureAwait(false);

            if (write.Success)
            {
                _indexETag = write.ETag;
                _cachedIndex = document;
                _logger.Info($"索引重建完成：{document.Games.Count} 个游戏");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn("写回重建后的索引失败（下次再试）", ex);
        }

        await _state.ReplaceRemoteAsync(document.Games, _clock.UtcNowMs, ct).ConfigureAwait(false);
        return document;
    }

    public async Task<IReadOnlyList<DeviceDocument>> RefreshDevicesAsync(CancellationToken ct)
    {
        try
        {
            // 先把自己写上去（并刷新在线时间），再列目录 —— 否则第一次调用永远看不到自己
            var local = await _devices.GetLocalAsync(ct).ConfigureAwait(false);
            await _remote.PutDeviceAsync(new DeviceDocument
            {
                Id = local.Id,
                Name = local.Name,
                LastSeenAt = _clock.UtcNowMs,
                Seq = local.Seq,
                AppVersion = AppVersion,
            }, ct).ConfigureAwait(false);
            await _devices.TouchAsync(ct).ConfigureAwait(false);

            var remoteDevices = await _remote.ListDevicesAsync(ct).ConfigureAwait(false);
            foreach (var device in remoteDevices)
            {
                await _devices.UpsertRemoteAsync(device, ct).ConfigureAwait(false);
            }

            return remoteDevices;
        }
        catch (RemoteNotConfiguredException)
        {
            // 未配置远端是正常状态（用户还没填 WebDAV），不是错误：
            // 每次后台轮询都打完整异常栈会把日志淹没。
            _logger.Debug("未配置 WebDAV，跳过刷新设备列表");
            return Array.Empty<DeviceDocument>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn("刷新设备列表失败", ex);
            return Array.Empty<DeviceDocument>();
        }
    }

    private async Task<Dictionary<Guid, SnapshotNode>> LoadGraphAsync(CancellationToken ct)
    {
        var nodes = await _snapshots.ListNodesAsync(ct).ConfigureAwait(false);
        return nodes.ToDictionary(n => n.Id);
    }

    /// <summary>两个快照之间是否存在父子关系（任一方向）。</summary>
    private static bool AreRelated(IReadOnlyDictionary<Guid, SnapshotNode> graph, Guid a, Guid b)
    {
        if (a == b)
        {
            return true;
        }

        return IsAncestor(graph, a, b) || IsAncestor(graph, b, a);
    }

    private static bool IsAncestor(IReadOnlyDictionary<Guid, SnapshotNode> graph, Guid ancestor, Guid node)
    {
        var current = node;
        for (var guard = 0; guard < 500; guard++)
        {
            if (!graph.TryGetValue(current, out var entry) || entry.ParentId is not { } parent)
            {
                return false;
            }

            if (parent == ancestor)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }
}
