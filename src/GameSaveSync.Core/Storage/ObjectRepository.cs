using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

public sealed class StoredObject
{
    public string Hash { get; init; } = string.Empty;
    public long Size { get; init; }
    public long StoredSize { get; init; }
    public int RefCount { get; init; }
    public bool Uploaded { get; init; }
}

public interface IObjectRepository
{
    /// <summary>登记对象；返回 true 表示此前不存在（CAS 中新写入一份）。</summary>
    Task<bool> RegisterAsync(string hash, long size, long storedSize, CancellationToken ct);

    /// <summary>引用计数 +1（同一对象被新快照引用）。</summary>
    Task AddReferenceAsync(string hash, CancellationToken ct);

    /// <summary>批量登记（单事务 + 预编译命令），大存档集的关键路径。</summary>
    Task RegisterManyAsync(IReadOnlyList<(string Hash, long Size)> items, CancellationToken ct);

    Task<StoredObject?> GetAsync(string hash, CancellationToken ct);

    Task MarkUploadedAsync(string hash, CancellationToken ct);

    Task<IReadOnlyList<string>> ListNotUploadedAsync(int limit, CancellationToken ct);

    Task<long> TotalStoredBytesAsync(CancellationToken ct);

    /// <summary>引用计数批量 -1；返回减到 0 的哈希（可安全删除）。</summary>
    Task<IReadOnlyList<string>> DecrementReferencesAsync(IReadOnlyList<string> hashes, CancellationToken ct);

    Task DeleteManyAsync(IReadOnlyList<string> hashes, CancellationToken ct);
}

public sealed class ObjectRepository : IObjectRepository
{
    private readonly SqliteDatabase _database;

    public ObjectRepository(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<bool> RegisterAsync(string hash, long size, long storedSize, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO objects(hash, size, stored_size, refcount, created_at, uploaded)
                VALUES($hash, $size, $storedSize, 1, $createdAt, 0)
                ON CONFLICT(hash) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$size", size);
            insert.Parameters.AddWithValue("$storedSize", storedSize);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var inserted = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            if (inserted == 0)
            {
                await using var bump = connection.CreateCommand();
                bump.CommandText = "UPDATE objects SET refcount = refcount + 1 WHERE hash = $hash";
                bump.Parameters.AddWithValue("$hash", hash);
                await bump.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return inserted > 0;
        }, ct);

    public Task RegisterManyAsync(IReadOnlyList<(string Hash, long Size)> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                "INSERT INTO objects(hash, size, stored_size, refcount, created_at, uploaded) " +
                "VALUES($hash, $size, $size, 1, $createdAt, 0) " +
                "ON CONFLICT(hash) DO UPDATE SET refcount = refcount + 1";
            var pHash = upsert.Parameters.Add("$hash", SqliteType.Text);
            var pSize = upsert.Parameters.Add("$size", SqliteType.Integer);
            var pCreated = upsert.Parameters.Add("$createdAt", SqliteType.Integer);
            pCreated.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 引用计数以"快照"为单位：同一快照里内容相同的多个文件只算一次引用。
            // 释放端（DecrementReferencesAsync）也是按去重后的 hash 减 1，
            // 两边必须对称，否则计数会漂移 → 对象永不回收（磁盘泄漏）或误删。
            foreach (var (hash, size) in items
                         .GroupBy(i => i.Hash, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (g.Key, Size: g.Max(i => i.Size))))
            {
                pHash.Value = hash;
                pSize.Value = size;
                await upsert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task AddReferenceAsync(string hash, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE objects SET refcount = refcount + 1 WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<StoredObject?> GetAsync(string hash, CancellationToken ct) =>
        _database.WithConnectionAsync<StoredObject?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash, size, stored_size, refcount, uploaded FROM objects WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new StoredObject
            {
                Hash = reader.GetString(0),
                Size = reader.GetInt64(1),
                StoredSize = reader.GetInt64(2),
                RefCount = reader.GetInt32(3),
                Uploaded = reader.GetInt32(4) != 0,
            };
        }, ct);

    public Task MarkUploadedAsync(string hash, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE objects SET uploaded = 1 WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<string>> ListNotUploadedAsync(int limit, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<string>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash FROM objects WHERE uploaded = 0 ORDER BY created_at LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var hashes = new List<string>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                hashes.Add(reader.GetString(0));
            }

            return hashes;
        }, ct);

    public Task<IReadOnlyList<string>> DecrementReferencesAsync(
        IReadOnlyList<string> hashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (hashes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        return _database.WithConnectionAsync<IReadOnlyList<string>>(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE objects SET refcount = refcount - 1 WHERE hash = $hash";
            var pHash = update.Parameters.Add("$hash", SqliteType.Text);

            foreach (var hash in hashes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                pHash.Value = hash;
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            // 只回收"本次请求里"的 hash：全库扫 refcount<=0 会把别的游戏
            // 遗留的零引用对象一起卷进来（本地 CAS 被删 + 远端被删 → 数据丢失）。
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                "SELECT hash FROM objects WHERE refcount <= 0 AND hash IN (SELECT value FROM json_each($hashes))";
            select.Parameters.AddWithValue(
                "$hashes",
                System.Text.Json.JsonSerializer.Serialize(
                    hashes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            var unreferenced = new List<string>();
            await using (var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    unreferenced.Add(reader.GetString(0));
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
            return (IReadOnlyList<string>)unreferenced;
        }, ct);
    }

    public Task DeleteManyAsync(IReadOnlyList<string> hashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        if (hashes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM objects WHERE hash = $hash";
            var pHash = command.Parameters.Add("$hash", SqliteType.Text);
            foreach (var hash in hashes)
            {
                pHash.Value = hash;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct);
    }

    public Task<long> TotalStoredBytesAsync(CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(stored_size), 0) FROM objects";
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);
}

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string? value, CancellationToken ct);

    /// <summary>本机设备 ID（GUID），首次启动生成并持久化。</summary>
    Task<string> GetDeviceIdAsync(CancellationToken ct);
}

public sealed class SettingsRepository : ISettingsRepository
{
    public const string DeviceIdKey = "device.id";
    public const string DeviceNameKey = "device.name";
    public const string WebDavUrlKey = "webdav.url";
    public const string WebDavUserKey = "webdav.user";
    public const string WebDavPasswordKey = "webdav.password";      // DPAPI 密文（base64）
    public const string WebDavRootKey = "webdav.root";
    public const string WebDavCertThumbKey = "webdav.cert.thumbprint";

    private readonly SqliteDatabase _database;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _deviceIdGate = new(1, 1);
    private string? _cachedDeviceId;

    public SettingsRepository(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<string?> GetAsync(string key, CancellationToken ct) =>
        _database.WithConnectionAsync<string?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is null or DBNull ? null : (string)result;
        }, ct);

    public Task SetAsync(string key, string? value, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value) VALUES($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public async Task<string> GetDeviceIdAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cachedDeviceId is not null)
            {
                return _cachedDeviceId;
            }
        }

        // 整个"读-判断-生成-写回"必须串行：
        // 并发首次调用时若各自生成 GUID，会互相覆盖，
        // 导致同一台设备对外表现出多个设备 ID（索引条目分裂、多设备识别错乱）。
        await _deviceIdGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_cachedDeviceId is not null)
                {
                    return _cachedDeviceId;
                }
            }

            var existing = await GetAsync(DeviceIdKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                lock (_gate)
                {
                    _cachedDeviceId = existing;
                }

                return existing;
            }

            var deviceId = Guid.NewGuid().ToString("D");
            await SetAsync(DeviceIdKey, deviceId, ct).ConfigureAwait(false);
            await SetAsync(DeviceNameKey, Environment.MachineName, ct).ConfigureAwait(false);

            // 回读确认：极端情况下（如多进程共享同一个库）可能有别的实例先写入，
            // 以库里的最终值为准，保证所有调用方拿到同一个 ID。
            var persisted = await GetAsync(DeviceIdKey, ct).ConfigureAwait(false);
            var finalId = string.IsNullOrWhiteSpace(persisted) ? deviceId : persisted;

            lock (_gate)
            {
                _cachedDeviceId = finalId;
            }

            return finalId;
        }
        finally
        {
            _deviceIdGate.Release();
        }
    }
}
