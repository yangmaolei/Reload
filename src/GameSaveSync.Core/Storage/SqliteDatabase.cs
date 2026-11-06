using Microsoft.Data.Sqlite;

namespace GameSaveSync.Core.Storage;

/// <summary>
/// SQLite（WAL）访问层。所有写操作幂等、进程可在任意点被杀。
/// 表结构一次建全（含 M2/M3 字段），避免后续改表。
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Schema.Ddl;
        command.ExecuteNonQuery();

        Migrations.Apply(connection);
    }

    public async Task<T> WithConnectionAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = OpenConnection();
        return await action(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task WithConnectionAsync(
        Func<SqliteConnection, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var connection = OpenConnection();
        await action(connection, cancellationToken).ConfigureAwait(false);
    }
}

internal static class Schema
{
    /// <summary>总纲 4.2 的 DDL，字段一个不少；仅追加了 last_error / skipped_count / uploaded / root_index。</summary>
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS games(
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          source TEXT NOT NULL,
          external_id TEXT,
          install_dir TEXT,
          exe_names TEXT NOT NULL,
          save_roots TEXT NOT NULL,
          cover_url TEXT,
          enabled INTEGER NOT NULL DEFAULT 1,
          retention TEXT,
          needs_relocate INTEGER DEFAULT 0,
          created_at INTEGER,
          updated_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS snapshots(
          id TEXT PRIMARY KEY,
          game_id TEXT NOT NULL REFERENCES games(id),
          parent_id TEXT,
          created_at INTEGER NOT NULL,
          trigger TEXT NOT NULL,
          device_id TEXT NOT NULL,
          manifest_hash TEXT NOT NULL,
          total_size INTEGER,
          file_count INTEGER,
          note TEXT,
          local_state TEXT,
          remote_state TEXT,
          conflict_group TEXT,
          last_error TEXT,
          skipped_count INTEGER DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS snapshot_files(
          snapshot_id TEXT REFERENCES snapshots(id),
          rel_path TEXT NOT NULL,
          rel_path_key TEXT NOT NULL,
          hash TEXT NOT NULL,
          size INTEGER,
          mtime INTEGER,
          attrs INTEGER,
          root_index INTEGER DEFAULT 0,
          PRIMARY KEY(snapshot_id, root_index, rel_path_key)
        );

        CREATE TABLE IF NOT EXISTS objects(
          hash TEXT PRIMARY KEY,
          size INTEGER,
          stored_size INTEGER,
          pack_id TEXT,
          refcount INTEGER,
          created_at INTEGER,
          verified_at INTEGER,
          uploaded INTEGER DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS outbox(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          op TEXT NOT NULL,
          payload TEXT NOT NULL,
          dedupe_key TEXT UNIQUE,
          attempts INTEGER DEFAULT 0,
          next_retry_at INTEGER,
          state TEXT DEFAULT 'pending',
          last_error TEXT,
          created_at INTEGER
        );

        CREATE TABLE IF NOT EXISTS devices(
          id TEXT PRIMARY KEY,
          name TEXT,
          last_seen_at INTEGER,
          seq INTEGER
        );

        -- M2：L2 指纹缓存（size/mtime/文件 ID/上次哈希），用于未变文件跳过读盘与启动补扫
        CREATE TABLE IF NOT EXISTS file_fingerprints(
          game_id TEXT NOT NULL,
          root_index INTEGER NOT NULL,
          rel_path_key TEXT NOT NULL,
          size INTEGER,
          mtime INTEGER,
          volume_serial INTEGER,
          file_id INTEGER,
          hash TEXT,
          PRIMARY KEY(game_id, root_index, rel_path_key)
        );

        CREATE TABLE IF NOT EXISTS settings(
          key TEXT PRIMARY KEY,
          value TEXT
        );

        -- M3：本机对各游戏的"最后发布"条目（index.json 合并的本地输入）
        CREATE TABLE IF NOT EXISTS index_state(
          game_id TEXT PRIMARY KEY,
          seq INTEGER NOT NULL,
          snapshot_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          updated_at INTEGER,
          conflict INTEGER DEFAULT 0
        );

        -- M3：最近一次合并后的远端索引视图（UI 的冲突徽标、下行去重都读它）
        CREATE TABLE IF NOT EXISTS remote_index(
          game_id TEXT PRIMARY KEY,
          seq INTEGER NOT NULL,
          snapshot_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          updated_at INTEGER,
          conflict INTEGER DEFAULT 0,
          synced_at INTEGER
        );

        -- M3：从远端见过的设备（devices/<id>.json），本机设备也记一行
        CREATE INDEX IF NOT EXISTS ix_snapshots_game_created ON snapshots(game_id, created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_snapshots_remote_state ON snapshots(remote_state);
        CREATE INDEX IF NOT EXISTS ix_snapshot_files_hash ON snapshot_files(hash);
        """;
}

/// <summary>
/// 轻量迁移：只做"加列"，不动已有数据。每次加字段都在这里补一行。
/// </summary>
internal static class Migrations
{
    public static void Apply(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        AddColumnIfMissing(connection, "games", "path_overrides", "TEXT");
        RebuildSnapshotFilesIfPrimaryKeyIsOld(connection);
    }

    /// <summary>
    /// 老库的 snapshot_files 主键是 (snapshot_id, rel_path_key)，不含 root_index。
    /// 同一个游戏的两个存档根下出现同名文件时（如两个根都有 save.dat）会撞主键，
    /// 导致整次快照插入失败。SQLite 不支持直接改主键，只能重建表。
    /// </summary>
    private static void RebuildSnapshotFilesIfPrimaryKeyIsOld(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        // 读 PRAGMA table_info：pk 列序号 > 0 的即为主键列，按 pk 序号升序
        var primaryKeyColumns = new List<(int PkIndex, string Name)>();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(snapshot_files)";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                var pk = reader.GetInt32(5);
                if (pk > 0)
                {
                    primaryKeyColumns.Add((pk, reader.GetString(1)));
                }
            }
        }

        primaryKeyColumns.Sort((a, b) => a.PkIndex.CompareTo(b.PkIndex));
        var pkNames = primaryKeyColumns.Select(c => c.Name).ToList();

        var alreadyMigrated = pkNames.Count == 3 &&
                              pkNames.Contains("root_index", StringComparer.OrdinalIgnoreCase);
        if (alreadyMigrated)
        {
            return;
        }

        using var rebuild = connection.CreateCommand();
        rebuild.CommandText = """
            PRAGMA foreign_keys=OFF;
            BEGIN;
            CREATE TABLE snapshot_files_new(
              snapshot_id TEXT REFERENCES snapshots(id),
              rel_path TEXT NOT NULL,
              rel_path_key TEXT NOT NULL,
              hash TEXT NOT NULL,
              size INTEGER,
              mtime INTEGER,
              attrs INTEGER,
              root_index INTEGER DEFAULT 0,
              PRIMARY KEY(snapshot_id, root_index, rel_path_key)
            );
            INSERT INTO snapshot_files_new(snapshot_id, rel_path, rel_path_key, hash, size, mtime, attrs, root_index)
              SELECT snapshot_id, rel_path, rel_path_key, hash, size, mtime, attrs, root_index FROM snapshot_files;
            DROP TABLE snapshot_files;
            ALTER TABLE snapshot_files_new RENAME TO snapshot_files;
            CREATE INDEX IF NOT EXISTS ix_snapshot_files_hash ON snapshot_files(hash);
            COMMIT;
            PRAGMA foreign_keys=ON;
            """;
        rebuild.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table,
        string column,
        string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }
}
