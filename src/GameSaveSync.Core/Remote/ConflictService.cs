using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Remote;

/// <summary>冲突一侧的展示数据（相对共同祖先的差异）。</summary>
public sealed record ConflictSide(
    Snapshot Snapshot,
    string DeviceName,
    int FileCount,
    long TotalSize,
    int AddedFiles,
    int ModifiedFiles,
    int DeletedFiles)
{
    public string Describe() =>
        $"{TimeUtil.FormatLocal(Snapshot.CreatedAt)} · {DeviceName} · " +
        $"{FileCount} 文件 / {Restore.RestorePlan.FormatBytes(TotalSize)} · " +
        $"相对祖先 +{AddedFiles} ~{ModifiedFiles} -{DeletedFiles}";
}

public sealed record ConflictView(
    Guid GameId,
    string GameName,
    ConflictSide Left,
    ConflictSide Right,
    Guid? AncestorId);

public interface IConflictService
{
    Task<ConflictView?> GetAsync(Guid gameId, CancellationToken ct);

    /// <summary>用户选定一侧：把该快照写回索引并清除 conflict 标记。</summary>
    Task<bool> ResolveAsync(Guid gameId, Guid chosenSnapshotId, CancellationToken ct);
}

/// <summary>
/// 冲突视图：index.conflict=true 时，把"本机这一侧"和"另一台设备那一侧"并排摆出来，
/// 各自相对共同祖先给出新增/修改/删除数；用户选择后调用 ResolveAsync 清标记。
/// 注意：这里只做展示与选择，**不做三向合并**（存档是二进制，合并没有意义）。
/// </summary>
public sealed class ConflictService : IConflictService
{
    private readonly IIndexStateRepository _state;
    private readonly ISnapshotRepository _snapshots;
    private readonly IGameRepository _games;
    private readonly IRemoteIndexService _index;
    private readonly IDeviceRepository _devices;
    private readonly IAppLogger _logger;

    public ConflictService(
        IIndexStateRepository state,
        ISnapshotRepository snapshots,
        IGameRepository games,
        IRemoteIndexService index,
        IDeviceRepository devices,
        IAppLogger logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConflictView?> GetAsync(Guid gameId, CancellationToken ct)
    {
        var entry = await _state.GetRemoteAsync(gameId, ct).ConfigureAwait(false);
        if (entry is null || !entry.Conflict)
        {
            return null;
        }

        var game = await _games.GetAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return null;
        }

        var all = await _snapshots.ListByGameAsync(gameId, ct).ConfigureAwait(false);
        if (all.Count < 2)
        {
            return null;
        }

        var primary = all.FirstOrDefault(s => s.Id == entry.SnapshotId)
                      ?? all.FirstOrDefault(s => s.DeviceId == entry.DeviceId)
                      ?? all[0];

        // 冲突的另一侧 = 最近的、来自别的设备的快照
        var other = all.FirstOrDefault(s => !string.Equals(s.DeviceId, primary.DeviceId, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(s => s.Id != primary.Id);

        if (other is null || other.Id == primary.Id)
        {
            return null;
        }

        var graph = (await _snapshots.ListNodesAsync(ct).ConfigureAwait(false)).ToDictionary(n => n.Id);
        var ancestorId = FindCommonAncestor(graph, primary.Id, other.Id);
        var ancestorFiles = ancestorId is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await _snapshots.GetFilesAsync(ancestorId.Value, ct).ConfigureAwait(false))
                .ToDictionary(f => $"{f.RootIndex}|{f.RelPathKey}", f => f.Hash, StringComparer.OrdinalIgnoreCase);

        var deviceNames = (await _devices.ListAsync(ct).ConfigureAwait(false))
            .ToDictionary(
                d => d.Id,
                // 设备 ID 长度不固定（远端可控），截取前 8 位要防越界
                d => string.IsNullOrWhiteSpace(d.Name) ? ShortId(d.Id) : d.Name,
                StringComparer.OrdinalIgnoreCase);

        var left = await BuildSideAsync(primary, ancestorFiles, deviceNames, ct).ConfigureAwait(false);
        var right = await BuildSideAsync(other, ancestorFiles, deviceNames, ct).ConfigureAwait(false);

        return new ConflictView(gameId, game.Name, left, right, ancestorId);
    }

    public async Task<bool> ResolveAsync(Guid gameId, Guid chosenSnapshotId, CancellationToken ct)
    {
        var snapshot = await _snapshots.GetAsync(chosenSnapshotId, ct).ConfigureAwait(false);
        if (snapshot is null || snapshot.GameId != gameId)
        {
            return false;
        }

        var ok = await _index.ResolveConflictAsync(gameId, chosenSnapshotId, ct).ConfigureAwait(false);
        if (ok)
        {
            _logger.Info($"用户选择保留快照 {chosenSnapshotId:N}，冲突标记已清除");
        }

        return ok;
    }

    private async Task<ConflictSide> BuildSideAsync(
        Snapshot snapshot,
        IReadOnlyDictionary<string, string> ancestorFiles,
        IReadOnlyDictionary<string, string> deviceNames,
        CancellationToken ct)
    {
        var files = await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false);
        var current = files.ToDictionary(f => $"{f.RootIndex}|{f.RelPathKey}", f => f.Hash, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var modified = 0;
        foreach (var (key, hash) in current)
        {
            if (!ancestorFiles.TryGetValue(key, out var ancestorHash))
            {
                added++;
            }
            else if (!string.Equals(ancestorHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                modified++;
            }
        }

        var deleted = ancestorFiles.Keys.Count(key => !current.ContainsKey(key));
        var name = deviceNames.TryGetValue(snapshot.DeviceId, out var deviceName)
            ? deviceName
            : ShortId(snapshot.DeviceId);

        return new ConflictSide(snapshot, name, snapshot.FileCount, snapshot.TotalSize, added, modified, deleted);
    }

    /// <summary>设备 ID 的短显示名：最多取前 8 位，短 ID 原样返回（不能越界）。</summary>
    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? "(未知设备)" : id[..Math.Min(8, id.Length)];

    private static Guid? FindCommonAncestor(
        IReadOnlyDictionary<Guid, SnapshotNode> graph,
        Guid left,
        Guid right)
    {
        var leftAncestors = new HashSet<Guid>();
        var current = left;
        for (var guard = 0; guard < 500; guard++)
        {
            leftAncestors.Add(current);
            if (!graph.TryGetValue(current, out var node) || node.ParentId is not { } parent)
            {
                break;
            }

            current = parent;
        }

        current = right;
        for (var guard = 0; guard < 500; guard++)
        {
            if (!graph.TryGetValue(current, out var node))
            {
                return null;
            }

            if (node.ParentId is not { } parent)
            {
                return leftAncestors.Contains(current) ? current : null;
            }

            current = parent;
            if (leftAncestors.Contains(current))
            {
                return current;
            }
        }

        return null;
    }
}
