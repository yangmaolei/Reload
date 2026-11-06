using GameSaveSync.Core.Models;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Restore;

public enum DiffChangeKind
{
    Added = 0,
    Modified = 1,
    Removed = 2,
}

public sealed record SnapshotDiffEntry(
    string RelPath,
    DiffChangeKind Kind,
    long SizeBefore,
    long SizeAfter,
    string? HashBefore,
    string? HashAfter)
{
    public long SizeDelta => SizeAfter - SizeBefore;

    public string KindText => Kind switch
    {
        DiffChangeKind.Added => "新增",
        DiffChangeKind.Modified => "修改",
        _ => "删除",
    };
}

/// <summary>相对上一快照的文件级差异（只看清单，不读对象内容）。</summary>
public sealed record SnapshotDiff(
    Guid SnapshotId,
    Guid? PreviousId,
    IReadOnlyList<SnapshotDiffEntry> Entries,
    long SizeBefore,
    long SizeAfter)
{
    public bool HasPrevious => PreviousId is not null;

    public int AddedCount => Entries.Count(e => e.Kind == DiffChangeKind.Added);

    public int ModifiedCount => Entries.Count(e => e.Kind == DiffChangeKind.Modified);

    public int RemovedCount => Entries.Count(e => e.Kind == DiffChangeKind.Removed);

    public long SizeDelta => SizeAfter - SizeBefore;

    public bool IsIdentical => Entries.Count == 0;
}

/// <summary>
/// 快照差异：按 <c>RelPathKey</c>（大小写不敏感）对齐两份清单，比较哈希判断"真的改了"。
/// 只用于展示，不参与同步决策。
/// </summary>
public sealed class SnapshotDiffService
{
    private readonly ISnapshotRepository _snapshots;

    public SnapshotDiffService(ISnapshotRepository snapshots) =>
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));

    /// <summary>与快照的 parent 比较；没有 parent 时与时间线上更早的最近一个比较。</summary>
    public async Task<SnapshotDiff> CompareWithPreviousAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"快照不存在：{snapshotId}");

        Guid? previousId = snapshot.ParentId;
        if (previousId is null)
        {
            var list = await _snapshots.ListByGameAsync(snapshot.GameId, ct).ConfigureAwait(false);
            previousId = list
                .Where(s => s.Id != snapshot.Id && s.CreatedAt <= snapshot.CreatedAt)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefault();
        }

        return await CompareAsync(snapshotId, previousId, ct).ConfigureAwait(false);
    }

    public async Task<SnapshotDiff> CompareAsync(Guid snapshotId, Guid? previousId, CancellationToken ct)
    {
        var current = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"快照不存在：{snapshotId}");

        if (previousId is null)
        {
            return new SnapshotDiff(snapshotId, null, Array.Empty<SnapshotDiffEntry>(), 0, current.TotalSize);
        }

        var previous = await _snapshots.GetAsync(previousId.Value, ct).ConfigureAwait(false);
        if (previous is null)
        {
            return new SnapshotDiff(snapshotId, null, Array.Empty<SnapshotDiffEntry>(), 0, current.TotalSize);
        }

        var beforeFiles = await _snapshots.GetFilesAsync(previous.Id, ct).ConfigureAwait(false);
        var afterFiles = await _snapshots.GetFilesAsync(current.Id, ct).ConfigureAwait(false);

        var before = beforeFiles.ToDictionary(f => f.RelPathKey, StringComparer.OrdinalIgnoreCase);
        var after = afterFiles.ToDictionary(f => f.RelPathKey, StringComparer.OrdinalIgnoreCase);

        var entries = new List<SnapshotDiffEntry>();

        foreach (var (key, file) in after)
        {
            if (!before.TryGetValue(key, out var old))
            {
                entries.Add(new SnapshotDiffEntry(file.RelPath, DiffChangeKind.Added, 0, file.Size, null, file.Hash));
                continue;
            }

            if (!string.Equals(old.Hash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new SnapshotDiffEntry(
                    file.RelPath, DiffChangeKind.Modified, old.Size, file.Size, old.Hash, file.Hash));
            }
        }

        foreach (var (key, file) in before)
        {
            if (!after.ContainsKey(key))
            {
                entries.Add(new SnapshotDiffEntry(file.RelPath, DiffChangeKind.Removed, file.Size, 0, file.Hash, null));
            }
        }

        entries.Sort((left, right) =>
        {
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0 ? kind : string.Compare(left.RelPath, right.RelPath, StringComparison.OrdinalIgnoreCase);
        });

        return new SnapshotDiff(snapshotId, previous.Id, entries, previous.TotalSize, current.TotalSize);
    }
}
