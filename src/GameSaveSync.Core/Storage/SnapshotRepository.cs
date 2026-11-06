using GameSaveSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

public interface ISnapshotRepository
{
    Task InsertAsync(Snapshot snapshot, IReadOnlyList<SnapshotFile> files, CancellationToken ct);

    Task<IReadOnlyList<Snapshot>> ListByGameAsync(Guid gameId, CancellationToken ct);

    Task<Snapshot?> GetAsync(Guid snapshotId, CancellationToken ct);

    Task<IReadOnlyList<SnapshotFile>> GetFilesAsync(Guid snapshotId, CancellationToken ct);

    /// <summary>该游戏最近的快照（按 created_at 降序，用于 parent_id 与"相对上一快照的差异"）。</summary>
    Task<Snapshot?> GetLatestAsync(Guid gameId, CancellationToken ct);

    Task SetRemoteStateAsync(Guid snapshotId, RemoteSnapshotState state, string? lastError, CancellationToken ct);

    /// <summary>所有待上传快照（M1 手动重试用；M2 换成 outbox 驱动）。</summary>
    Task<IReadOnlyList<Snapshot>> ListPendingAsync(CancellationToken ct);

    Task<int> CountAsync(Guid gameId, CancellationToken ct);

    /// <summary>保留策略清理：软删除（保留元数据用于时间线/冲突历史）。</summary>
    Task MarkPrunedAsync(Guid snapshotId, CancellationToken ct);

    /// <summary>按触发类型计数（调度器/测试用来判断"某类快照出现了几次"）。</summary>
    Task<int> CountByTriggerAsync(Guid gameId, SnapshotTrigger trigger, CancellationToken ct);

    /// <summary>时间线骨架（id / parent / createdAt），给 index.json 的父子关系判定用。</summary>
    Task<IReadOnlyList<SnapshotNode>> ListNodesAsync(CancellationToken ct);
}

public sealed record SnapshotNode(Guid Id, Guid? ParentId, long CreatedAt, Guid GameId);

public sealed class SnapshotRepository : ISnapshotRepository
{
    private const string SelectColumns = """
        id, game_id, parent_id, created_at, trigger, device_id, manifest_hash,
        total_size, file_count, note, local_state, remote_state, conflict_group, last_error, skipped_count
        """;

    private readonly SqliteDatabase _database;

    public SnapshotRepository(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task InsertAsync(Snapshot snapshot, IReadOnlyList<SnapshotFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(files);

        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var insertSnapshot = connection.CreateCommand())
            {
                insertSnapshot.Transaction = transaction;
                insertSnapshot.CommandText = """
                    INSERT INTO snapshots(id, game_id, parent_id, created_at, trigger, device_id, manifest_hash,
                                          total_size, file_count, note, local_state, remote_state,
                                          conflict_group, last_error, skipped_count)
                    VALUES($id, $gameId, $parentId, $createdAt, $trigger, $deviceId, $manifestHash,
                           $totalSize, $fileCount, $note, $localState, $remoteState,
                           $conflictGroup, $lastError, $skippedCount)
                    """;
                insertSnapshot.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
                insertSnapshot.Parameters.AddWithValue("$gameId", snapshot.GameId.ToString("D"));
                insertSnapshot.Parameters.AddWithValue("$parentId",
                    snapshot.ParentId.HasValue ? snapshot.ParentId.Value.ToString("D") : (object)DBNull.Value);
                insertSnapshot.Parameters.AddWithValue("$createdAt", snapshot.CreatedAt);
                insertSnapshot.Parameters.AddWithValue("$trigger", snapshot.Trigger.ToDbValue());
                insertSnapshot.Parameters.AddWithValue("$deviceId", snapshot.DeviceId);
                insertSnapshot.Parameters.AddWithValue("$manifestHash", snapshot.ManifestHash);
                insertSnapshot.Parameters.AddWithValue("$totalSize", snapshot.TotalSize);
                insertSnapshot.Parameters.AddWithValue("$fileCount", snapshot.FileCount);
                insertSnapshot.Parameters.AddWithValue("$note", (object?)snapshot.Note ?? DBNull.Value);
                insertSnapshot.Parameters.AddWithValue("$localState", snapshot.LocalState.ToDbValue());
                insertSnapshot.Parameters.AddWithValue("$remoteState", snapshot.RemoteState.ToDbValue());
                insertSnapshot.Parameters.AddWithValue("$conflictGroup",
                    (object?)snapshot.ConflictGroup ?? DBNull.Value);
                insertSnapshot.Parameters.AddWithValue("$lastError", (object?)snapshot.LastError ?? DBNull.Value);
                insertSnapshot.Parameters.AddWithValue("$skippedCount", snapshot.SkippedCount);
                await insertSnapshot.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var insertFile = connection.CreateCommand())
            {
                insertFile.Transaction = transaction;
                insertFile.CommandText = """
                    INSERT INTO snapshot_files(snapshot_id, rel_path, rel_path_key, hash, size, mtime, attrs, root_index)
                    VALUES($snapshotId, $relPath, $relPathKey, $hash, $size, $mtime, $attrs, $rootIndex)
                    """;
                var pSnapshotId = insertFile.Parameters.Add("$snapshotId", SqliteType.Text);
                var pRelPath = insertFile.Parameters.Add("$relPath", SqliteType.Text);
                var pRelPathKey = insertFile.Parameters.Add("$relPathKey", SqliteType.Text);
                var pHash = insertFile.Parameters.Add("$hash", SqliteType.Text);
                var pSize = insertFile.Parameters.Add("$size", SqliteType.Integer);
                var pMtime = insertFile.Parameters.Add("$mtime", SqliteType.Integer);
                var pAttrs = insertFile.Parameters.Add("$attrs", SqliteType.Integer);
                var pRootIndex = insertFile.Parameters.Add("$rootIndex", SqliteType.Integer);

                foreach (var file in files)
                {
                    pSnapshotId.Value = snapshot.Id.ToString("D");
                    pRelPath.Value = file.RelPath;
                    pRelPathKey.Value = file.RelPathKey;
                    pHash.Value = file.Hash;
                    pSize.Value = file.Size;
                    pMtime.Value = file.Mtime;
                    pAttrs.Value = file.Attrs;
                    pRootIndex.Value = file.RootIndex;
                    await insertFile.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<Snapshot>> ListByGameAsync(Guid gameId, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<Snapshot>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SelectColumns} FROM snapshots
                WHERE game_id = $gameId AND local_state = 'present'
                ORDER BY created_at DESC, rowid DESC
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var snapshots = new List<Snapshot>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                snapshots.Add(Read(reader));
            }

            return snapshots;
        }, ct);

    public Task<Snapshot?> GetAsync(Guid snapshotId, CancellationToken ct) =>
        _database.WithConnectionAsync<Snapshot?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM snapshots WHERE id = $id";
            command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
        }, ct);

    public Task<IReadOnlyList<SnapshotFile>> GetFilesAsync(Guid snapshotId, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<SnapshotFile>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rel_path, rel_path_key, hash, size, mtime, attrs, root_index
                FROM snapshot_files WHERE snapshot_id = $id
                ORDER BY root_index, rel_path
                """;
            command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var files = new List<SnapshotFile>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                files.Add(new SnapshotFile
                {
                    SnapshotId = snapshotId,
                    RelPath = reader.GetString(0),
                    RelPathKey = reader.GetString(1),
                    Hash = reader.GetString(2),
                    Size = reader.GetInt64(3),
                    Mtime = reader.GetInt64(4),
                    Attrs = reader.GetInt32(5),
                    RootIndex = reader.GetInt32(6),
                });
            }

            return files;
        }, ct);

    public Task<Snapshot?> GetLatestAsync(Guid gameId, CancellationToken ct) =>
        _database.WithConnectionAsync<Snapshot?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SelectColumns} FROM snapshots
                WHERE game_id = $gameId AND local_state = 'present'
                ORDER BY created_at DESC, rowid DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
        }, ct);

    public Task SetRemoteStateAsync(Guid snapshotId, RemoteSnapshotState state, string? lastError, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET remote_state = $state, last_error = $err WHERE id = $id";
            command.Parameters.AddWithValue("$state", state.ToDbValue());
            command.Parameters.AddWithValue("$err", (object?)lastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<Snapshot>> ListPendingAsync(CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<Snapshot>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SelectColumns} FROM snapshots
                WHERE remote_state = 'pending' AND local_state = 'present'
                ORDER BY created_at ASC
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var snapshots = new List<Snapshot>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                snapshots.Add(Read(reader));
            }

            return snapshots;
        }, ct);

    public Task<IReadOnlyList<SnapshotNode>> ListNodesAsync(CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<SnapshotNode>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, parent_id, created_at, game_id FROM snapshots WHERE local_state = 'present'";
            var result = new List<SnapshotNode>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result.Add(new SnapshotNode(
                    Guid.Parse(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                    reader.GetInt64(2),
                    Guid.Parse(reader.GetString(3))));
            }

            return result;
        }, ct);

    public Task<int> CountByTriggerAsync(Guid gameId, SnapshotTrigger trigger, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(1) FROM snapshots " +
                "WHERE game_id = $gameId AND trigger = $trigger AND local_state = 'present'";
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            command.Parameters.AddWithValue("$trigger", trigger.ToDbValue());
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    public Task MarkPrunedAsync(Guid snapshotId, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET local_state = 'pruned' WHERE id = $id";
            command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<int> CountAsync(Guid gameId, CancellationToken ct) =>        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM snapshots WHERE game_id = $gameId AND local_state = 'present'";
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    private static Snapshot Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        GameId = Guid.Parse(reader.GetString(1)),
        ParentId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        CreatedAt = reader.GetInt64(3),
        Trigger = SnapshotTriggerNames.Parse(reader.GetString(4)),
        DeviceId = reader.GetString(5),
        ManifestHash = reader.GetString(6),
        TotalSize = reader.GetInt64(7),
        FileCount = reader.GetInt32(8),
        Note = reader.IsDBNull(9) ? null : reader.GetString(9),
        LocalState = SnapshotStates.ParseLocal(reader.GetString(10)),
        RemoteState = SnapshotStates.ParseRemote(reader.GetString(11)),
        ConflictGroup = reader.IsDBNull(12) ? null : reader.GetString(12),
        LastError = reader.IsDBNull(13) ? null : reader.GetString(13),
        SkippedCount = reader.GetInt32(14),
    };
}
