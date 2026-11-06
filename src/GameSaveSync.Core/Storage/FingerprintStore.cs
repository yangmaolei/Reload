using GameSaveSync.Core.Detection;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

public sealed class FingerprintStore : IFingerprintStore
{
    private readonly SqliteDatabase _database;

    public FingerprintStore(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<IReadOnlyDictionary<string, FileFingerprint>> LoadAsync(Guid gameId, CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyDictionary<string, FileFingerprint>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT root_index, rel_path_key, size, mtime, volume_serial, file_id, hash
                FROM file_fingerprints WHERE game_id = $gameId
                """;
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));

            var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var key = FingerprintKeys.Build(reader.GetInt32(0), reader.GetString(1));
                result[key] = new FileFingerprint(
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    (ulong)reader.GetInt64(4),
                    (ulong)reader.GetInt64(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6));
            }

            return result;
        }, ct);

    public Task SaveAsync(Guid gameId, IReadOnlyList<FingerprintRecord> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);

        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM file_fingerprints WHERE game_id = $gameId";
                delete.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (items.Count > 0)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO file_fingerprints(game_id, root_index, rel_path_key, size, mtime, volume_serial, file_id, hash)
                    VALUES($gameId, $root, $key, $size, $mtime, $volume, $fileId, $hash)
                    ON CONFLICT(game_id, root_index, rel_path_key) DO UPDATE SET
                      size = excluded.size, mtime = excluded.mtime,
                      volume_serial = excluded.volume_serial, file_id = excluded.file_id, hash = excluded.hash
                    """;
                var pGame = insert.Parameters.Add("$gameId", SqliteType.Text);
                var pRoot = insert.Parameters.Add("$root", SqliteType.Integer);
                var pKey = insert.Parameters.Add("$key", SqliteType.Text);
                var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
                var pMtime = insert.Parameters.Add("$mtime", SqliteType.Integer);
                var pVolume = insert.Parameters.Add("$volume", SqliteType.Integer);
                var pFileId = insert.Parameters.Add("$fileId", SqliteType.Integer);
                var pHash = insert.Parameters.Add("$hash", SqliteType.Text);

                pGame.Value = gameId.ToString("D");
                foreach (var item in items)
                {
                    pRoot.Value = item.RootIndex;
                    pKey.Value = item.RelPathKey;
                    pSize.Value = item.Size;
                    pMtime.Value = item.MtimeMs;
                    pVolume.Value = (long)item.VolumeSerial;
                    pFileId.Value = (long)item.FileId;
                    pHash.Value = item.Hash;
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task ClearAsync(Guid gameId, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM file_fingerprints WHERE game_id = $gameId";
            command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
}
