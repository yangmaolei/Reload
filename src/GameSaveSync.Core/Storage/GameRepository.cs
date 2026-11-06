using GameSaveSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

public interface IGameRepository
{
    Task<IReadOnlyList<Game>> ListAsync(CancellationToken ct);

    Task<Game?> GetAsync(Guid id, CancellationToken ct);

    Task UpsertAsync(Game game, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    Task SetNeedsRelocateAsync(Guid id, bool needsRelocate, CancellationToken ct);
}

public sealed class GameRepository : IGameRepository
{
    private const string SelectColumns = """
        id, name, source, external_id, install_dir, exe_names, save_roots, cover_url,
        enabled, retention, needs_relocate, created_at, updated_at, path_overrides, poster_kind
        """;

    private readonly SqliteDatabase _database;

    public GameRepository(SqliteDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<IReadOnlyList<Game>> ListAsync(CancellationToken ct) =>
        _database.WithConnectionAsync<IReadOnlyList<Game>>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM games ORDER BY name COLLATE NOCASE";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var games = new List<Game>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                games.Add(Read(reader));
            }

            return games;
        }, ct);

    public Task<Game?> GetAsync(Guid id, CancellationToken ct) =>
        _database.WithConnectionAsync<Game?>(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SelectColumns} FROM games WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
        }, ct);

    public Task UpsertAsync(Game game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO games(id, name, source, external_id, install_dir, exe_names, save_roots,
                                  cover_url, enabled, retention, needs_relocate, created_at, updated_at,
                                  path_overrides, poster_kind)
                VALUES($id, $name, $source, $externalId, $installDir, $exeNames, $saveRoots,
                       $coverUrl, $enabled, $retention, $needsRelocate, $createdAt, $updatedAt,
                       $pathOverrides, $posterKind)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name,
                  source = excluded.source,
                  external_id = excluded.external_id,
                  install_dir = excluded.install_dir,
                  exe_names = excluded.exe_names,
                  save_roots = excluded.save_roots,
                  cover_url = excluded.cover_url,
                  enabled = excluded.enabled,
                  retention = excluded.retention,
                  needs_relocate = excluded.needs_relocate,
                  updated_at = excluded.updated_at,
                  path_overrides = excluded.path_overrides,
                  poster_kind = excluded.poster_kind
                """;
            command.Parameters.AddWithValue("$id", game.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", game.Name);
            command.Parameters.AddWithValue("$source", game.Source.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$externalId", (object?)game.ExternalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$installDir", (object?)game.InstallDir ?? DBNull.Value);
            command.Parameters.AddWithValue("$exeNames", JsonColumns.SerializeStringList(game.ExeNames));
            command.Parameters.AddWithValue("$saveRoots", JsonColumns.SerializeSaveRoots(game.SaveRoots));
            command.Parameters.AddWithValue("$coverUrl", (object?)game.CoverUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", game.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$retention", JsonColumns.Serialize(game.Retention));
            command.Parameters.AddWithValue("$needsRelocate", game.NeedsRelocate ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", game.CreatedAt);
            command.Parameters.AddWithValue("$updatedAt", game.UpdatedAt);
            command.Parameters.AddWithValue(
                "$pathOverrides",
                game.PathOverrides.Count == 0 ? (object)DBNull.Value : JsonColumns.Serialize(game.PathOverrides));
            command.Parameters.AddWithValue("$posterKind", (object?)game.PosterKind ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);

            await using (var deleteFiles = connection.CreateCommand())
            {
                deleteFiles.Transaction = (SqliteTransaction)transaction;
                deleteFiles.CommandText = """
                    DELETE FROM snapshot_files WHERE snapshot_id IN
                      (SELECT id FROM snapshots WHERE game_id = $id)
                    """;
                deleteFiles.Parameters.AddWithValue("$id", id.ToString("D"));
                await deleteFiles.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var deleteSnapshots = connection.CreateCommand())
            {
                deleteSnapshots.Transaction = (SqliteTransaction)transaction;
                deleteSnapshots.CommandText = "DELETE FROM snapshots WHERE game_id = $id";
                deleteSnapshots.Parameters.AddWithValue("$id", id.ToString("D"));
                await deleteSnapshots.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using (var deleteGame = connection.CreateCommand())
            {
                deleteGame.Transaction = (SqliteTransaction)transaction;
                deleteGame.CommandText = "DELETE FROM games WHERE id = $id";
                deleteGame.Parameters.AddWithValue("$id", id.ToString("D"));
                await deleteGame.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, ct);

    public Task SetNeedsRelocateAsync(Guid id, bool needsRelocate, CancellationToken ct) =>
        _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE games SET needs_relocate = $flag WHERE id = $id";
            command.Parameters.AddWithValue("$flag", needsRelocate ? 1 : 0);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);

    internal static Game Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Source = Enum.TryParse<GameSource>(reader.GetString(2), true, out var source) ? source : GameSource.Manual,
        ExternalId = reader.IsDBNull(3) ? null : reader.GetString(3),
        InstallDir = reader.IsDBNull(4) ? null : reader.GetString(4),
        ExeNames = JsonColumns.DeserializeStringList(reader.GetString(5)),
        SaveRoots = JsonColumns.DeserializeSaveRoots(reader.GetString(6)),
        CoverUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
        Enabled = reader.GetInt32(8) != 0,
        Retention = JsonColumns.Deserialize<RetentionPolicy>(reader.IsDBNull(9) ? null : reader.GetString(9))
                    ?? new RetentionPolicy(),
        NeedsRelocate = reader.GetInt32(10) != 0,
        CreatedAt = reader.GetInt64(11),
        UpdatedAt = reader.GetInt64(12),
        PathOverrides = reader.FieldCount > 13 && !reader.IsDBNull(13)
            ? JsonColumns.Deserialize<Dictionary<string, string>>(reader.GetString(13))
              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        // poster_kind 是后加的列，老库里没有这一列时 FieldCount 会更小
        PosterKind = reader.FieldCount > 14 && !reader.IsDBNull(14)
            ? reader.GetString(14)
            : null,
    };
}
