namespace GameSaveSync.Core.Remote;

public sealed class IndexMergeRequest
{
    /// <summary>从远端读到的最新索引（解析失败时为 null，调用方应先重建）。</summary>
    public required IndexDocument Remote { get; init; }

    /// <summary>本机对各个游戏已发布过的条目（只包含本机管过的游戏）。</summary>
    public required IReadOnlyDictionary<string, IndexGameEntry> Local { get; init; }

    public required string LocalDeviceId { get; init; }

    /// <summary>两个快照之间是否存在父子关系（本地时间线可判定的部分）。</summary>
    public required Func<Guid, Guid, bool> AreRelated { get; init; }

    /// <summary>快照创建时间（用于冲突时挑"较新"的那个）。</summary>
    public required Func<Guid, long?> CreatedAtOf { get; init; }

    public long NowMs { get; init; }
}

public sealed record IndexMergeResult(
    IndexDocument Merged,
    IReadOnlyList<IndexConflict> Conflicts,
    bool Changed);

/// <summary>
/// index.json 的逐游戏合并（M3 的核心状态机）。
///
/// 规则（严格按方案，任何分支都**不删除任何快照**）：
/// <list type="number">
/// <item>只有远端有 → 用远端。</item>
/// <item>只有本地有 → 用本地。</item>
/// <item>远端 deviceId == 本机：本条是自家历史，seq 大者胜（正常推进）。</item>
/// <item>本地 seq &gt; 远端 seq 且两者快照有父子关系 → 用本地（正常推进）。</item>
/// <item>本地 seq &gt; 远端 seq 且无父子关系 → 双方快照都保留，conflict=true，
///       snapshotId 取 created_at 较新者。</item>
/// <item>其余（本地 seq ≤ 远端 seq）→ 用远端（别的设备更新）。</item>
/// </list>
/// 冲突只影响"默认推荐哪个版本"，不影响数据是否还在 —— 两个快照都在远端。
/// </summary>
public static class IndexMerger
{
    public static IndexMergeResult Merge(IndexMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Remote);
        ArgumentNullException.ThrowIfNull(request.Local);

        var merged = new IndexDocument
        {
            Version = IndexDocument.CurrentVersion,
            UpdatedAt = request.NowMs,
        };

        var conflicts = new List<IndexConflict>();
        var changed = false;

        var keys = new HashSet<string>(request.Remote.Games.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Local.Keys)
        {
            keys.Add(key);
        }

        foreach (var key in keys)
        {
            request.Remote.Games.TryGetValue(key, out var remoteEntry);
            request.Local.TryGetValue(key, out var localEntry);

            IndexGameEntry winner;
            bool conflict;

            if (localEntry is null)
            {
                winner = remoteEntry!.Clone();
                conflict = remoteEntry!.Conflict;
            }
            else if (remoteEntry is null)
            {
                winner = localEntry.Clone();
                conflict = localEntry.Conflict;
            }
            else if (string.Equals(remoteEntry.DeviceId, request.LocalDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                // 自家历史：seq 大者胜
                winner = localEntry.Seq > remoteEntry.Seq ? localEntry.Clone() : remoteEntry.Clone();
                conflict = winner.Conflict;
            }
            else if (localEntry.Seq > remoteEntry.Seq)
            {
                if (request.AreRelated(localEntry.SnapshotId, remoteEntry.SnapshotId))
                {
                    winner = localEntry.Clone();          // 正常推进：本地接了远端那个快照的后代
                    conflict = false;
                }
                else
                {
                    // 分叉：两个快照都保留，标记冲突，默认推荐较新的
                    var localAt = request.CreatedAtOf(localEntry.SnapshotId) ?? 0;
                    var remoteAt = request.CreatedAtOf(remoteEntry.SnapshotId) ?? 0;
                    winner = localAt >= remoteAt ? localEntry.Clone() : remoteEntry.Clone();
                    winner.Seq = Math.Max(localEntry.Seq, remoteEntry.Seq);
                    winner.Conflict = true;
                    conflict = true;

                    conflicts.Add(new IndexConflict(key, localEntry.Clone(), remoteEntry.Clone(), winner.Clone()));
                }
            }
            else
            {
                winner = remoteEntry.Clone();             // 别的设备更新
                conflict = remoteEntry.Conflict;
            }

            winner.Conflict = conflict;
            merged.Games[key] = winner;

            var before = remoteEntry ?? localEntry;
            if (before is null ||
                before.Seq != winner.Seq ||
                before.SnapshotId != winner.SnapshotId ||
                !string.Equals(before.DeviceId, winner.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                before.Conflict != winner.Conflict)
            {
                changed = true;
            }
        }

        return new IndexMergeResult(merged, conflicts, changed);
    }
}
