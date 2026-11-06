using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

public sealed record DeviceRecord(string Id, string Name, long LastSeenAt, long Seq)
{
    public DeviceDocument ToDocument() => new()
    {
        Id = Id,
        Name = Name,
        LastSeenAt = LastSeenAt,
        Seq = Seq,
    };
}

/// <summary>
/// 设备表：本机设备 + 从远端见过的设备。<c>seq</c> 是每设备单调递增计数器，
/// index.json 的合并完全依赖它来判定"谁更新"。
/// </summary>
public interface IDeviceRepository
{
    Task<DeviceRecord> GetLocalAsync(CancellationToken ct);

    /// <summary>取下一个序号并立即持久化（+1）。</summary>
    Task<long> NextSeqAsync(CancellationToken ct);

    /// <summary>
    /// 取"逻辑时钟"意义上的下一个序号：<c>max(本机已用, 远端见过的) + 1</c>。
    /// 只有这样才能用 seq 比较"两台设备谁更新"（每设备各自从 1 开始的话永远比不出来）。
    /// </summary>
    Task<long> NextSeqAtLeastAsync(long minimum, CancellationToken ct);

    Task TouchAsync(CancellationToken ct);

    Task<IReadOnlyList<DeviceRecord>> ListAsync(CancellationToken ct);

    Task UpsertRemoteAsync(DeviceDocument device, CancellationToken ct);

    Task<string> GetLocalIdAsync(CancellationToken ct);
}

public sealed class DeviceRepository : IDeviceRepository
{
    private readonly SqliteDatabase _database;
    private readonly ISettingsRepository _settings;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _seqGate = new(1, 1);

    public DeviceRepository(SqliteDatabase database, ISettingsRepository settings, IClock clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<string> GetLocalIdAsync(CancellationToken ct) =>
        await _settings.GetDeviceIdAsync(ct).ConfigureAwait(false);

    public async Task<DeviceRecord> GetLocalAsync(CancellationToken ct)
    {
        var id = await GetLocalIdAsync(ct).ConfigureAwait(false);
        var name = await _settings.GetAsync(SettingsRepository.DeviceNameKey, ct).ConfigureAwait(false)
                   ?? Environment.MachineName;

        return await _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, last_seen_at, seq FROM devices WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return new DeviceRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? name : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
            }

            return new DeviceRecord(id, name, 0, 0);
        }, ct).ConfigureAwait(false);
    }

    public Task<long> NextSeqAsync(CancellationToken ct) => NextSeqAtLeastAsync(0, ct);

    public async Task<long> NextSeqAtLeastAsync(long minimum, CancellationToken ct)
    {
        // 同一进程内串行，避免两次发布拿到同一个 seq
        await _seqGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = await GetLocalIdAsync(ct).ConfigureAwait(false);
            var name = await _settings.GetAsync(SettingsRepository.DeviceNameKey, ct).ConfigureAwait(false)
                       ?? Environment.MachineName;
            var now = _clock.UtcNowMs;

            return await _database.WithConnectionAsync(async (connection, token) =>
            {
                await using var upsert = connection.CreateCommand();
                upsert.CommandText = """
                    INSERT INTO devices(id, name, last_seen_at, seq) VALUES($id, $name, $now, MAX(1, $min + 1))
                    ON CONFLICT(id) DO UPDATE SET
                        seq = MAX(COALESCE(devices.seq, 0), $min) + 1,
                        last_seen_at = $now
                    """;
                upsert.Parameters.AddWithValue("$id", id);
                upsert.Parameters.AddWithValue("$name", name);
                upsert.Parameters.AddWithValue("$now", now);
                upsert.Parameters.AddWithValue("$min", minimum);
                await upsert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT seq FROM devices WHERE id = $id";
                read.Parameters.AddWithValue("$id", id);
                var value = await read.ExecuteScalarAsync(token).ConfigureAwait(false);
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _seqGate.Release();
        }
    }

    public async Task TouchAsync(CancellationToken ct)
    {
        var id = await GetLocalIdAsync(ct).ConfigureAwait(false);
        await _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices(id, name, last_seen_at, seq) VALUES($id, $name, $now, 0)
                ON CONFLICT(id) DO UPDATE SET last_seen_at = $now
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", Environment.MachineName);
            command.Parameters.AddWithValue("$now", _clock.UtcNowMs);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DeviceRecord>> ListAsync(CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<DeviceRecord>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, last_seen_at, seq FROM devices ORDER BY last_seen_at DESC";
            var result = new List<DeviceRecord>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result.Add(new DeviceRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
            }

            return result;
        }, ct);

    public Task UpsertRemoteAsync(DeviceDocument device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices(id, name, last_seen_at, seq) VALUES($id, $name, $seen, $seq)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name,
                                              last_seen_at = excluded.last_seen_at,
                                              seq = MAX(COALESCE(devices.seq, 0), excluded.seq)
                """;
            command.Parameters.AddWithValue("$id", device.Id);
            command.Parameters.AddWithValue("$name", device.Name);
            command.Parameters.AddWithValue("$seen", device.LastSeenAt);
            command.Parameters.AddWithValue("$seq", device.Seq);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }
}

/// <summary>本地"已发布条目"与"远端索引视图"两张表的读写。</summary>
public interface IIndexStateRepository
{
    Task<IReadOnlyDictionary<string, IndexGameEntry>> LoadLocalAsync(CancellationToken ct);

    Task SaveLocalAsync(Guid gameId, IndexGameEntry entry, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IndexGameEntry>> LoadRemoteAsync(CancellationToken ct);

    Task ReplaceRemoteAsync(IReadOnlyDictionary<string, IndexGameEntry> entries, long syncedAt, CancellationToken ct);

    Task<IndexGameEntry?> GetRemoteAsync(Guid gameId, CancellationToken ct);
}

public sealed class IndexStateRepository : IIndexStateRepository
{
    private readonly SqliteDatabase _database;

    public IndexStateRepository(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<IReadOnlyDictionary<string, IndexGameEntry>> LoadLocalAsync(CancellationToken ct) =>
        LoadAsync("index_state", ct);

    public Task<IReadOnlyDictionary<string, IndexGameEntry>> LoadRemoteAsync(CancellationToken ct) =>
        LoadAsync("remote_index", ct);

    public Task SaveLocalAsync(Guid gameId, IndexGameEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO index_state(game_id, seq, snapshot_id, device_id, updated_at, conflict)
                VALUES($gameId, $seq, $snapshotId, $deviceId, $updatedAt, $conflict)
                ON CONFLICT(game_id) DO UPDATE SET seq = excluded.seq,
                                                  snapshot_id = excluded.snapshot_id,
                                                  device_id = excluded.device_id,
                                                  updated_at = excluded.updated_at,
                                                  conflict = excluded.conflict
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            command.Parameters.AddWithValue("$seq", entry.Seq);
            command.Parameters.AddWithValue("$snapshotId", entry.SnapshotId.ToString("D"));
            command.Parameters.AddWithValue("$deviceId", entry.DeviceId);
            command.Parameters.AddWithValue("$updatedAt", entry.UpdatedAt);
            command.Parameters.AddWithValue("$conflict", entry.Conflict ? 1 : 0);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task ReplaceRemoteAsync(
        IReadOnlyDictionary<string, IndexGameEntry> entries,
        long syncedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM remote_index";
                await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (entries.Count > 0)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO remote_index(game_id, seq, snapshot_id, device_id, updated_at, conflict, synced_at)
                    VALUES($gameId, $seq, $snapshotId, $deviceId, $updatedAt, $conflict, $syncedAt)
                    """;
                foreach (var (gameId, entry) in entries)
                {
                    insert.Parameters.Clear();
                    insert.Parameters.AddWithValue("$gameId", gameId);
                    insert.Parameters.AddWithValue("$seq", entry.Seq);
                    insert.Parameters.AddWithValue("$snapshotId", entry.SnapshotId.ToString("D"));
                    insert.Parameters.AddWithValue("$deviceId", entry.DeviceId);
                    insert.Parameters.AddWithValue("$updatedAt", entry.UpdatedAt);
                    insert.Parameters.AddWithValue("$conflict", entry.Conflict ? 1 : 0);
                    insert.Parameters.AddWithValue("$syncedAt", syncedAt);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IndexGameEntry?> GetRemoteAsync(Guid gameId, CancellationToken ct) =>
        _database.WithConnectionAsync<IndexGameEntry?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT seq, snapshot_id, device_id, updated_at, conflict FROM remote_index WHERE game_id = $gameId";
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new IndexGameEntry
            {
                Seq = reader.GetInt64(0),
                SnapshotId = Guid.Parse(reader.GetString(1)),
                DeviceId = reader.GetString(2),
                UpdatedAt = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                Conflict = !reader.IsDBNull(4) && reader.GetInt32(4) != 0,
            };
        }, ct);

    private Task<IReadOnlyDictionary<string, IndexGameEntry>> LoadAsync(string table, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyDictionary<string, IndexGameEntry>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT game_id, seq, snapshot_id, device_id, updated_at, conflict FROM {table}";
            var result = new Dictionary<string, IndexGameEntry>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = new IndexGameEntry
                {
                    Seq = reader.GetInt64(1),
                    SnapshotId = Guid.Parse(reader.GetString(2)),
                    DeviceId = reader.GetString(3),
                    UpdatedAt = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    Conflict = !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                };
            }

            return result;
        }, ct);
}
