using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.Core.Retention;

/// <summary>
/// 清理计划（两阶段的第一阶段）：要删哪些快照、预计释放多少空间。
/// UI 必须先给用户看这个计划并确认。
/// </summary>
public sealed class RetentionPlan
{
    public Guid GameId { get; init; }

    public string GameName { get; init; } = string.Empty;

    public IReadOnlyList<Snapshot> Snapshots { get; init; } = Array.Empty<Snapshot>();

    /// <summary>清理后会变成"零引用"的对象。</summary>
    public IReadOnlyList<string> ObjectsToDelete { get; init; } = Array.Empty<string>();

    public long BytesFreed { get; init; }

    public int FileCount { get; init; }

    public bool IsEmpty => Snapshots.Count == 0;

    public string Describe() =>
        IsEmpty
            ? "没有符合清理条件的快照"
            : $"将清理 {Snapshots.Count} 个旧快照（{FileCount} 个文件），预计释放 {Restore.RestorePlan.FormatBytes(BytesFreed)}，" +
              $"同时删除 {ObjectsToDelete.Count} 个无人引用的对象";
}

public sealed record RetentionResult(int PrunedSnapshots, int DeletedObjects, long BytesFreed);

public interface IRetentionService
{
    Task<RetentionPlan> PreviewAsync(Guid gameId, CancellationToken ct);

    Task<RetentionResult> ApplyAsync(RetentionPlan plan, CancellationToken ct);

    /// <summary>按策略直接预览+执行（后台自动清理用）。</summary>
    Task<RetentionResult> RunAsync(Guid gameId, CancellationToken ct);
}

/// <summary>
/// GFS 保留策略：最近 N 个 + 每日 1 个 × 7 天 + 每周 1 个 × 4 周。
/// 永不自动删除：manual 快照（用户手动打点）、7 天内的 pre_restore、以及**最新的那一个**。
/// 清理顺序：本地快照引用（软删）→ 清单文件 → 引用计数 → CAS 对象 → 远端（进 outbox 重试）。
/// </summary>
public sealed class RetentionService : IRetentionService
{
    private readonly IGameRepository _games;
    private readonly ISnapshotRepository _snapshots;
    private readonly IObjectRepository _objects;
    private readonly IObjectStore _store;
    private readonly ISnapshotManifestStore _manifests;
    private readonly IOutboxStore _outbox;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;

    public RetentionService(
        IGameRepository games,
        ISnapshotRepository snapshots,
        IObjectRepository objects,
        IObjectStore store,
        ISnapshotManifestStore manifests,
        IOutboxStore outbox,
        IClock clock,
        IAppLogger logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>pre_restore 快照的保留期（超过才允许清理）。</summary>
    public TimeSpan PreRestoreRetention { get; set; } = TimeSpan.FromDays(7);

    public async Task<RetentionPlan> PreviewAsync(Guid gameId, CancellationToken ct)
    {
        var game = await _games.GetAsync(gameId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"游戏不存在：{gameId}");

        var all = await _snapshots.ListByGameAsync(gameId, ct).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return new RetentionPlan { GameId = gameId, GameName = game.Name };
        }

        var policy = game.Retention;
        var keep = SelectKeepSet(all, policy);

        var prune = all.Where(s => !keep.Contains(s.Id)).ToList();
        if (prune.Count == 0)
        {
            return new RetentionPlan { GameId = gameId, GameName = game.Name };
        }

        // 哪些对象在清理后变成零引用：所有快照里对这些 hash 的引用数 == 被清理快照里的引用数
        var keptHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in all.Where(s => keep.Contains(s.Id)))
        {
            foreach (var file in await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false))
            {
                keptHashes.Add(file.Hash);
            }
        }

        var prunedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var fileCount = 0;
        foreach (var snapshot in prune)
        {
            var files = await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false);
            foreach (var file in files)
            {
                prunedHashes.Add(file.Hash);
                bytes += file.Size;
                fileCount++;
            }
        }

        var deletable = prunedHashes.Where(h => !keptHashes.Contains(h)).ToList();

        return new RetentionPlan
        {
            GameId = gameId,
            GameName = game.Name,
            Snapshots = prune,
            ObjectsToDelete = deletable,
            BytesFreed = bytes,
            FileCount = fileCount,
        };
    }

    public async Task<RetentionResult> ApplyAsync(RetentionPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty)
        {
            return new RetentionResult(0, 0, 0);
        }

        var hashesToRelease = new List<string>();

        foreach (var snapshot in plan.Snapshots)
        {
            var files = await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false);
            hashesToRelease.AddRange(files.Select(f => f.Hash));

            await _snapshots.MarkPrunedAsync(snapshot.Id, ct).ConfigureAwait(false);
            _manifests.Delete(plan.GameId, SnapshotNames.ManifestFileName(snapshot));

            // 远端清单删除失败不影响本地清理，进 outbox 重试
            await _outbox.EnqueueAsync(
                OutboxOperations.DeleteManifest,
                OutboxPayloads.ForManifest(plan.GameId, snapshot.Id, SnapshotNames.ManifestFileName(snapshot)),
                $"delman:{plan.GameId:D}:{SnapshotNames.ManifestFileName(snapshot)}",
                forceDue: false,
                ct).ConfigureAwait(false);
        }

        var unreferenced = await _objects.DecrementReferencesAsync(hashesToRelease, ct).ConfigureAwait(false);

        var deletedObjects = 0;
        foreach (var hash in unreferenced)
        {
            if (_store.Delete(hash))
            {
                deletedObjects++;
            }

            await _outbox.EnqueueAsync(
                OutboxOperations.DeleteObject,
                OutboxPayloads.ForObject(hash),
                $"delobj:{hash}",
                forceDue: false,
                ct).ConfigureAwait(false);
        }

        await _objects.DeleteManyAsync(unreferenced, ct).ConfigureAwait(false);

        _logger.Info(
            $"保留策略：清理 {plan.Snapshots.Count} 个快照，删除 {deletedObjects} 个对象，" +
            $"释放 {plan.BytesFreed} 字节");

        return new RetentionResult(plan.Snapshots.Count, deletedObjects, plan.BytesFreed);
    }

    public async Task<RetentionResult> RunAsync(Guid gameId, CancellationToken ct)
    {
        var plan = await PreviewAsync(gameId, ct).ConfigureAwait(false);
        return await ApplyAsync(plan, ct).ConfigureAwait(false);
    }

    /// <summary>GFS 选择要保留的快照集合。</summary>
    private HashSet<Guid> SelectKeepSet(IReadOnlyList<Snapshot> all, RetentionPolicy policy)
    {
        var keep = new HashSet<Guid>();
        var now = _clock.Now;

        // 0) 最新的那个永远保留（用户看到的"当前状态"不能被清掉）
        keep.Add(all[0].Id);

        // 1) 最近 N 个
        foreach (var snapshot in all.Take(Math.Max(0, policy.KeepLast)))
        {
            keep.Add(snapshot.Id);
        }

        // 2) 每日 1 个 × N 天（按本地日期，最近 N 天）
        var dailyKept = 0;
        var seenDays = new HashSet<DateOnly>();
        foreach (var snapshot in all)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedAt).ToLocalTime();
            if ((now - local).TotalDays > policy.Daily)
            {
                break;
            }

            var day = DateOnly.FromDateTime(local.DateTime);
            if (seenDays.Add(day))
            {
                keep.Add(snapshot.Id);
                if (++dailyKept >= policy.Daily)
                {
                    break;
                }
            }
        }

        // 3) 每周 1 个 × N 周（ISO 周）
        var weeklyKept = 0;
        var seenWeeks = new HashSet<(int Year, int Week)>();
        foreach (var snapshot in all)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedAt).ToLocalTime();
            if ((now - local).TotalDays > policy.Weekly * 7)
            {
                break;
            }

            var week = System.Globalization.ISOWeek.GetYear(local.DateTime);
            var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(local.DateTime);
            if (seenWeeks.Add((week, weekNumber)))
            {
                keep.Add(snapshot.Id);
                if (++weeklyKept >= policy.Weekly)
                {
                    break;
                }
            }
        }

        // 4) 永不自动删：manual 全部；pre_restore 未到期
        foreach (var snapshot in all)
        {
            if (snapshot.Trigger == SnapshotTrigger.Manual)
            {
                keep.Add(snapshot.Id);
                continue;
            }

            if (snapshot.Trigger == SnapshotTrigger.PreRestore)
            {
                var age = now - DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedAt);
                if (age < PreRestoreRetention)
                {
                    keep.Add(snapshot.Id);
                }
            }
        }

        return keep;
    }
}
