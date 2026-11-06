using System.Text.Json;
using GameSaveSync.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Sync;

/// <summary>outbox 里的操作类型（字符串常量，写库后不可改）。</summary>
public static class OutboxOperations
{
    public const string PutObject = "put_object";
    public const string PutManifest = "put_manifest";
    public const string DeleteObject = "delete_object";
    public const string DeleteManifest = "delete_manifest";

    /// <summary>M3：把本机快照发布进 index.json（乐观锁由 RemoteIndexService 处理）。</summary>
    public const string PutIndex = "put_index";
}

public static class OutboxStates
{
    public const string Pending = "pending";
    public const string Inflight = "inflight";
    public const string Failed = "failed";
}

public sealed class OutboxItem
{
    public long Id { get; init; }
    public string Op { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string? DedupeKey { get; init; }
    public int Attempts { get; init; }
    public long? NextRetryAt { get; init; }
    public string State { get; init; } = OutboxStates.Pending;
    public string? LastError { get; init; }
    public long CreatedAt { get; init; }
}

public sealed record OutboxFailure(OutboxItem Item, string Error);

public sealed record OutboxRunResult(int Processed, int Succeeded, int Failed, int PermanentlyFailed);

/// <summary>
/// 持久化 outbox：所有网络操作先落库再执行，进程被杀后能重放。
/// dedupe_key 保证"同一对象只排一次"（内容寻址对象天然幂等，重复排只是浪费带宽）。
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// 入队；已存在同 dedupe_key 的待办时返回 false（不重复排队）。
    /// <paramref name="forceDue"/> 为 true 时（用户点了"重试"）把重试时间提前到当前，不重置次数。
    /// </summary>
    Task<bool> EnqueueAsync(string op, string payload, string dedupeKey, bool forceDue, CancellationToken ct);

    /// <summary>取一条到期的待办并标记 inflight（attempts 已 +1）。</summary>
    Task<OutboxItem?> LeaseDueAsync(long nowMs, CancellationToken ct);

    Task CompleteAsync(long id, CancellationToken ct);

    Task FailAsync(long id, string error, long nextRetryAtMs, bool permanent, CancellationToken ct);

    /// <summary>启动时把 inflight 视为未完成，重新入队（对象按 hash 命名，重复 PUT 无害）。</summary>
    Task<int> RequeueInflightAsync(CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);

    Task<int> CountFailedAsync(CancellationToken ct);

    Task ResetFailedAsync(CancellationToken ct);

    Task<IReadOnlyList<OutboxItem>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>最近一条错误信息（用于把失败原因显示到快照上）。</summary>
    Task<string?> GetLastErrorAsync(CancellationToken ct);
}

public sealed class SqliteOutboxStore : IOutboxStore
{
    private readonly SqliteDatabase _database;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);

    public SqliteOutboxStore(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<bool> EnqueueAsync(string op, string payload, string dedupeKey, bool forceDue, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);
        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = forceDue
                ? """
                  INSERT INTO outbox(op, payload, dedupe_key, attempts, next_retry_at, state, created_at)
                  VALUES($op, $payload, $dedupeKey, 0, 0, 'pending', $createdAt)
                  ON CONFLICT(dedupe_key) DO UPDATE SET
                    next_retry_at = 0,
                    state = 'pending',
                    attempts = 0,
                    last_error = NULL
                  """
                : """
                  INSERT INTO outbox(op, payload, dedupe_key, attempts, next_retry_at, state, created_at)
                  VALUES($op, $payload, $dedupeKey, 0, 0, 'pending', $createdAt)
                  ON CONFLICT(dedupe_key) DO NOTHING
                  """;
            command.Parameters.AddWithValue("$op", op);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$dedupeKey", dedupeKey);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return affected > 0;
        }, ct);
    }

    public async Task<OutboxItem?> LeaseDueAsync(long nowMs, CancellationToken ct)
    {
        // 串行化租约，避免后台循环与手动"立即重试"同时抢同一条
        await _leaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _database.WithConnectionAsync<OutboxItem?>(async (connection, token) =>
            {
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(token).ConfigureAwait(false);

                long id;
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = """
                        SELECT id FROM outbox
                        WHERE state = 'pending' AND (next_retry_at IS NULL OR next_retry_at <= $now)
                        ORDER BY id LIMIT 1
                        """;
                    select.Parameters.AddWithValue("$now", nowMs);
                    var result = await select.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result is null || result is DBNull)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return null;
                    }

                    id = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE outbox SET state = 'inflight', attempts = attempts + 1 WHERE id = $id";
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                OutboxItem item;
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = """
                        SELECT id, op, payload, dedupe_key, attempts, next_retry_at, state, last_error, created_at
                        FROM outbox WHERE id = $id
                        """;
                    read.Parameters.AddWithValue("$id", id);
                    await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return null;
                    }

                    item = Read(reader);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return item;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    public Task CompleteAsync(long id, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM outbox WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task FailAsync(long id, string error, long nextRetryAtMs, bool permanent, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET state = $state, last_error = $error, next_retry_at = $next
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$state", permanent ? OutboxStates.Failed : OutboxStates.Pending);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$next", nextRetryAtMs);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<int> RequeueInflightAsync(CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(1) FROM outbox WHERE state = 'inflight'";
            var inflight = Convert.ToInt32(
                await count.ExecuteScalarAsync(token).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);

            // 启动重放：inflight 视为未完成，pending 也一并提前到"现在"
            // （失败次数 attempts 保留，所以连续失败的项仍会在达到上限后标记 failed）
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE outbox SET state = 'pending', next_retry_at = 0 WHERE state IN ('inflight', 'pending')";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            return inflight;
        }, ct);

    public Task<int> CountPendingAsync(CancellationToken ct) =>
        CountAsync("state = 'pending'", ct);

    public Task<int> CountFailedAsync(CancellationToken ct) =>
        CountAsync("state = 'failed'", ct);

    public Task ResetFailedAsync(CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox SET state = 'pending', attempts = 0, next_retry_at = 0 WHERE state = 'failed'
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<OutboxItem>> ListRecentAsync(int limit, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<OutboxItem>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, op, payload, dedupe_key, attempts, next_retry_at, state, last_error, created_at
                FROM outbox ORDER BY id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var items = new List<OutboxItem>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                items.Add(Read(reader));
            }

            return items;
        }, ct);

    public Task<string?> GetLastErrorAsync(CancellationToken ct) =>
        _database.WithConnectionAsync<string?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT last_error FROM outbox WHERE last_error IS NOT NULL ORDER BY id DESC LIMIT 1";
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is null or DBNull ? null : (string)result;
        }, ct);

    private Task<int> CountAsync(string where, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM outbox WHERE {where}";
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);

    private static OutboxItem Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Op = reader.GetString(1),
        Payload = reader.GetString(2),
        DedupeKey = reader.IsDBNull(3) ? null : reader.GetString(3),
        Attempts = reader.GetInt32(4),
        NextRetryAt = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        State = reader.GetString(6),
        LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = reader.GetInt64(8),
    };
}

/// <summary>
/// 指数退避：1s → 2 → 4 … 封顶 5min，带 ±20% 随机抖动（避免多设备同时重连）。
/// </summary>
public static class OutboxBackoff
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan Compute(int attempts, Random? random = null)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var seconds = BaseDelay.TotalSeconds * Math.Pow(2, exponent);
        var capped = Math.Min(seconds, MaxDelay.TotalSeconds);

        var jitter = (random ?? Random.Shared).NextDouble() * 0.4 - 0.2;   // ±20%

        // 封顶必须在抖动之后：否则 +20% 的抖动会把上限推到 6 分钟，
        // 与"封顶 5min"的约定不符（重试等待时间超出预期）。
        var actual = Math.Min(capped * (1 + jitter), MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(0.2, actual));
    }
}

/// <summary>outbox 里 payload 的强类型载体。</summary>
public sealed class PutObjectPayload
{
    public string Hash { get; set; } = string.Empty;
}

public sealed class ManifestPayload
{
    public Guid GameId { get; set; }

    public Guid SnapshotId { get; set; }

    public string FileName { get; set; } = string.Empty;
}

public static class OutboxPayloads
{
    public static string ForObject(string hash) =>
        JsonSerializer.Serialize(new PutObjectPayload { Hash = hash }, JsonColumns.Options);

    public static string ForManifest(Guid gameId, Guid snapshotId, string fileName) =>
        JsonSerializer.Serialize(
            new ManifestPayload { GameId = gameId, SnapshotId = snapshotId, FileName = fileName },
            JsonColumns.Options);

    public static PutObjectPayload ParseObject(string payload) =>
        JsonSerializer.Deserialize<PutObjectPayload>(payload, JsonColumns.Options)
        ?? throw new InvalidDataException($"outbox payload 非法：{payload}");

    public static ManifestPayload ParseManifest(string payload) =>
        JsonSerializer.Deserialize<ManifestPayload>(payload, JsonColumns.Options)
        ?? throw new InvalidDataException($"outbox payload 非法：{payload}");
}
