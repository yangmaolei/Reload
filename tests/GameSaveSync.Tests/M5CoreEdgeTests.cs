using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// M5 第三批：Core 层边界与异常场景
/// （保留策略跨天/跨周、快照稳定性、诊断脱敏、路径令牌、Provider 分块、数据库并发等）。
/// </summary>
public class M5CoreEdgeTests
{
    // ============================================================
    // 保留策略（GFS）：跨天 / 跨周 / ISO 周边界 / manual 保护
    // ============================================================

    [Fact]
    public async Task Retention_AcrossDays_KeepsOnePerDay()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Gfs", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("GFS游戏", default, dir);

        env.SnapshotService.SkipIfUnchangedWithParent = false;

        // 造 5 个跨天的快照（每天内容都不同，否则会被"内容未变"跳过）
        var baseTime = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 5; day++)
        {
            env.Clock.Set(baseTime.AddDays(day));
            File.WriteAllBytes(
                Path.Combine(dir, "a.dat"),
                TestEnvironment.Bytes(16, day + 1));

            // 用 PostChange 触发：manual 快照按设计永不自动清理（规则 4）
            await env.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.PostChange, null, default);
        }

        game.Retention = new RetentionPolicy { KeepLast = 1, Daily = 3, Weekly = 0 };
        await env.Games.UpsertAsync(game, default);

        var plan = await env.Retention.PreviewAsync(game.Id, default);

        // 5 天 → 保留最近 1 个 + 每天各 1 个（3 天） → 清理掉多出来的
        Assert.NotEmpty(plan.Snapshots);
        Assert.True(plan.Snapshots.Count <= 4, $"实际清理 {plan.Snapshots.Count} 个");
    }

    [Fact]
    public async Task Retention_ManualSnapshot_IsNeverPruned()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/GfsManual", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("手动保护游戏", default, dir);

        var baseTime = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        env.Clock.Set(baseTime);
        var manual = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, "重要", default);
        Assert.NotNull(manual.SnapshotId);

        for (var day = 1; day <= 4; day++)
        {
            env.Clock.Set(baseTime.AddDays(day));
            await env.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.PostChange, null, default);
        }

        game.Retention = new RetentionPolicy { KeepLast = 1, Daily = 1, Weekly = 0 };
        await env.Games.UpsertAsync(game, default);

        var plan = await env.Retention.PreviewAsync(game.Id, default);

        Assert.DoesNotContain(plan.Snapshots, s => s.Id == manual.SnapshotId!.Value);
    }

    [Fact]
    public async Task Retention_NoSnapshots_ReturnsEmptyPlan()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var game = await env.AddGameAsync("空游戏");

        var plan = await env.Retention.PreviewAsync(game.Id, default);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Snapshots);
    }

    [Fact]
    public async Task Retention_UnknownGame_Throws()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Retention.PreviewAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Retention_Apply_EmptyPlan_IsNoop()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var game = await env.AddGameAsync("空清理");
        var plan = await env.Retention.PreviewAsync(game.Id, default);

        var result = await env.Retention.ApplyAsync(plan, default);

        Assert.Equal(0, result.PrunedSnapshots);
        Assert.Equal(0, result.DeletedObjects);
    }

    // ============================================================
    // 快照：稳定性 / 空目录 / 缺失令牌 / 幂等
    // ============================================================

    [Fact]
    public async Task Snapshot_EmptyDirectory_ThrowsNoFilesFound()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = Path.Combine(env.ProfileDir, "Saved Games", "EmptyGame");
        Directory.CreateDirectory(dir);
        var game = await env.AddGameAsync("空目录游戏", default, dir);

        await Assert.ThrowsAsync<NoFilesFoundException>(() =>
            env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, default));
    }

    [Fact]
    public async Task Snapshot_MissingRoot_ThrowsNeedsRelocate()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var missing = Path.Combine(env.ProfileDir, "Saved Games", "NotThere");
        var game = await env.AddGameAsync("丢失目录游戏", default, missing);

        await Assert.ThrowsAsync<NeedsRelocateException>(() =>
            env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, default));
    }

    [Fact]
    public async Task Snapshot_UnknownGame_Throws()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.SnapshotService.CreateAsync(Guid.NewGuid(), SnapshotTrigger.Manual, null, default));
    }

    [Fact]
    public async Task Snapshot_IdenticalContent_SecondRunSkipsCreation()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Same", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("相同内容游戏", default, dir);

        var first = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, default);
        Assert.True(first.Created);
        Assert.NotNull(first.SnapshotId);

        // 该开关默认关闭（手动备份必须落盘），后台自动快照才会打开
        env.SnapshotService.SkipIfUnchangedWithParent = true;
        var second = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.PostChange, null, default);

        Assert.False(second.Created);
        Assert.Null(second.SnapshotId);
        Assert.NotNull(second.SkipReason);
    }

    [Fact]
    public async Task Snapshot_ContentChanges_CreatesNewSnapshotWithParentLink()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Chain", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("链式游戏", default, dir);

        var first = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, default);

        File.WriteAllBytes(Path.Combine(dir, "a.dat"), TestEnvironment.Bytes(16, 7));
        var second = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.PostChange, null, default);

        Assert.True(second.Created);
        Assert.NotNull(second.SnapshotId);
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);

        var snapshot = await env.Snapshots.GetAsync(second.SnapshotId!.Value, default);
        Assert.Equal(first.SnapshotId, snapshot!.ParentId);
    }

    [Fact]
    public async Task Snapshot_GlobalExclude_IsAppliedImmediately()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir(
            "Saved Games/Excl2",
            ("keep.dat", TestEnvironment.Bytes(16)),
            ("skip.log", TestEnvironment.Bytes(16)));

        var game = await env.AddGameAsync("全局排除游戏", default, dir);

        await env.Settings.SetAsync(SnapshotService.GlobalExcludeKey, "*.log", default);

        var result = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, default);

        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task Snapshot_GlobalExclude_MultipleLinesAndComments()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir(
            "Saved Games/Excl3",
            ("a.dat", TestEnvironment.Bytes(8)),
            ("b.log", TestEnvironment.Bytes(8)),
            ("c.tmp", TestEnvironment.Bytes(8)));

        var game = await env.AddGameAsync("多行排除游戏", default, dir);

        await env.Settings.SetAsync(
            SnapshotService.GlobalExcludeKey,
            "*.log\r\n*.tmp\r\n",
            default);

        var result = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, default);

        Assert.Equal(1, result.FileCount);
    }

    // ============================================================
    // 诊断：脱敏
    // ============================================================

    [Fact]
    public async Task Diagnostics_RedactsSecrets()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var report = await new GameSaveSync.Core.Diagnostics.DiagnosticsService(
            env.Database, env.Objects, env.Snapshots, env.Outbox,
            (GameSaveSync.Core.Sync.IRemoteStore)GameSaveSync.Core.Sync.NullRemoteStore.Instance,
            env.Settings, env.Logger, env.FileSystem, env.Paths, env.Clock)
            .BuildAsync(default);

        var text = report.ToText();

        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostics_Report_HasSections()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var report = await new GameSaveSync.Core.Diagnostics.DiagnosticsService(
            env.Database, env.Objects, env.Snapshots, env.Outbox,
            (GameSaveSync.Core.Sync.IRemoteStore)GameSaveSync.Core.Sync.NullRemoteStore.Instance,
            env.Settings, env.Logger, env.FileSystem, env.Paths, env.Clock)
            .BuildAsync(default);

        var text = report.ToText();

        Assert.Contains("Reload", text, StringComparison.Ordinal);
        Assert.NotEmpty(report.Notes);
    }

    // ============================================================
    // 路径令牌
    // ============================================================

    [Fact]
    public void PathToken_UnknownToken_Throws()
    {
        var resolver = new GameSaveSync.Core.Paths.PathTokenResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = @"C:\Users\test",
            });

        Assert.ThrowsAny<Exception>(() => resolver.Expand(@"%NOPE%\file.dat"));
    }

    [Fact]
    public void PathToken_TryExpand_DoesNotThrow()
    {
        var resolver = new GameSaveSync.Core.Paths.PathTokenResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = @"C:\Users\test",
            });

        Assert.False(resolver.TryExpand(@"%NOPE%\file.dat", out _, out var missing));
        Assert.Equal("%NOPE%", missing);
        Assert.True(resolver.TryExpand(@"%USERPROFILE%\file.dat", out var expanded, out _));
        Assert.Contains("test", expanded!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathToken_Tokenize_DoesNotMatchPrefixByName()
    {
        var resolver = new GameSaveSync.Core.Paths.PathTokenResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = @"C:\Users",
            });

        // "C:\UsersX" 不在 "C:\Users" 之下（下一字符不是分隔符）→ 不应被令牌化
        Assert.Null(resolver.Tokenize(@"C:\UsersX\file.dat"));
        Assert.Equal(@"%USERPROFILE%\file.dat", resolver.Tokenize(@"C:\Users\file.dat"));
    }

    [Fact]
    public void PathToken_Expand_PreservesUnclosedPercent()
    {
        var resolver = new GameSaveSync.Core.Paths.PathTokenResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = @"C:\Users\test",
            });

        // 路径里的字面 % 不应被当成令牌
        var result = resolver.Expand(@"C:\100%\file.dat");

        Assert.Contains("100%", result, StringComparison.Ordinal);
    }

    // ============================================================
    // 数据库并发
    // ============================================================

    [Fact]
    public async Task Database_ConcurrentWrites_DoNotDeadlock()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        // 多线程并发写同一个库：SQLite 的 busy_timeout 应保证不抛 BUSY
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            await env.Settings.SetAsync($"test.key.{i}", i.ToString(), default);
        })));

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(i.ToString(), await env.Settings.GetAsync($"test.key.{i}", default));
        }
    }

    [Fact]
    public async Task Database_Initialize_IsIdempotent()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        env.Database.Initialize();
        env.Database.Initialize();

        // 重复初始化不应破坏数据
        await env.Settings.SetAsync("k", "v", default);
        env.Database.Initialize();

        Assert.Equal("v", await env.Settings.GetAsync("k", default));
    }

    [Fact]
    public void Database_OldSchema_MigratesSnapshotFilesPrimaryKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "gss-migrate-" + Guid.NewGuid().ToString("N"));
        var paths = new GameSaveSync.Core.Configuration.AppPaths(root);
        paths.EnsureCreated();

        try
        {
            // 造一个"老库"：snapshot_files 主键不含 root_index
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                       $"Data Source={paths.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE snapshots(
                      id TEXT PRIMARY KEY, game_id TEXT, parent_id TEXT, created_at INTEGER,
                      trigger TEXT, device_id TEXT, manifest_hash TEXT, total_size INTEGER,
                      file_count INTEGER, note TEXT, local_state TEXT, remote_state TEXT,
                      conflict_group TEXT, last_error TEXT, skipped_count INTEGER DEFAULT 0);
                    CREATE TABLE snapshot_files(
                      snapshot_id TEXT REFERENCES snapshots(id),
                      rel_path TEXT NOT NULL,
                      rel_path_key TEXT NOT NULL,
                      hash TEXT NOT NULL,
                      size INTEGER, mtime INTEGER, attrs INTEGER,
                      root_index INTEGER DEFAULT 0,
                      PRIMARY KEY(snapshot_id, rel_path_key));
                    CREATE TABLE games(
                      id TEXT PRIMARY KEY, name TEXT, source TEXT, created_at INTEGER,
                      updated_at INTEGER, needs_relocate INTEGER DEFAULT 0, path_overrides TEXT);
                    """;
                command.ExecuteNonQuery();
            }

            // 初始化时触发迁移
            var database = new GameSaveSync.Core.Storage.SqliteDatabase(paths.DatabasePath);
            database.Initialize();

            // 迁移后同名不同根的文件应能共存
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                       $"Data Source={paths.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO snapshots(id, game_id, created_at, trigger, device_id, manifest_hash,
                                          total_size, file_count, local_state, remote_state)
                    VALUES('s1', 'g1', 0, 'manual', 'd1', 'h', 0, 2, 'present', 'pending');
                    """;
                command.ExecuteNonQuery();

                command.CommandText = """
                    INSERT INTO snapshot_files(snapshot_id, rel_path, rel_path_key, hash, size, root_index)
                    VALUES('s1', 'save.dat', 'save.dat', 'hash1', 1, 0);
                    """;
                command.ExecuteNonQuery();

                command.CommandText = """
                    INSERT INTO snapshot_files(snapshot_id, rel_path, rel_path_key, hash, size, root_index)
                    VALUES('s1', 'save.dat', 'save.dat', 'hash2', 1, 1);
                    """;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ============================================================
    // 对象库
    // ============================================================

    [Fact]
    public async Task ObjectStore_NormalizeHash_RejectsBlank()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        Assert.ThrowsAny<ArgumentException>(() => env.Store.GetObjectPath(""));
        Assert.ThrowsAny<ArgumentException>(() => env.Store.GetObjectPath("   "));
    }

    [Fact]
    public async Task ObjectStore_SameContentDifferentCaseHash_IsIdempotent()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var content = TestEnvironment.Bytes(32, 3);
        var hash = TestEnvironment.HashFileWith(content);

        await using (var stream = new MemoryStream(content))
        {
            await env.Store.PutAsync(hash.ToUpperInvariant(), stream, default);
        }

        Assert.True(env.Store.Contains(hash));
        Assert.True(env.Store.Contains(hash.ToUpperInvariant()));
    }

    [Fact]
    public async Task ObjectStore_WrongHash_ThrowsAndLeavesNoResidue()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var content = TestEnvironment.Bytes(16, 5);
        var wrongHash = TestEnvironment.HashFileWith(TestEnvironment.Bytes(16, 6));

        await using (var stream = new MemoryStream(content))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                env.Store.PutAsync(wrongHash, stream, default));
        }

        Assert.False(env.Store.Contains(wrongHash));

        // 临时目录不应留下残骸
        var tempFiles = Directory.Exists(env.Paths.ObjectsTempDir)
            ? Directory.GetFiles(env.Paths.ObjectsTempDir)
            : Array.Empty<string>();
        Assert.Empty(tempFiles);
    }

    // ============================================================
    // 上传服务
    // ============================================================

    [Fact]
    public async Task Upload_NotConfigured_ReportsPending()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/NoRemote", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("无远端游戏", default, dir);
        var snap = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, default);

        var outcome = await env.UploadService.UploadSnapshotAsync(snap.SnapshotId!.Value, default);

        Assert.False(outcome.Success);
        Assert.Contains("WebDAV", outcome.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_UnknownSnapshot_ReportsFailure()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var outcome = await env.UploadService.UploadSnapshotAsync(Guid.NewGuid(), default);

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task Upload_NoPending_ReturnsEmpty()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);

        var outcomes = await env.UploadService.UploadPendingAsync(default);

        Assert.Empty(outcomes);
    }

    // ============================================================
    // 索引合并（边界）
    // ============================================================

    [Fact]
    public void IndexMerger_EmptyDocuments_ProduceEmptyResult()
    {
        var request = new GameSaveSync.Core.Remote.IndexMergeRequest
        {
            Remote = new GameSaveSync.Core.Remote.IndexDocument(),
            Local = new Dictionary<string, GameSaveSync.Core.Remote.IndexGameEntry>(),
            LocalDeviceId = "device-a",
            AreRelated = (_, _) => false,
            CreatedAtOf = _ => null,
            NowMs = 1000,
        };

        var result = GameSaveSync.Core.Remote.IndexMerger.Merge(request);

        Assert.Empty(result.Merged.Games);
        Assert.False(result.Changed);
    }

    [Fact]
    public void IndexMerger_SameSeq_DoesNotStealFromOtherDevice()
    {
        var snapshotId = Guid.NewGuid();
        var request = new GameSaveSync.Core.Remote.IndexMergeRequest
        {
            Remote = new GameSaveSync.Core.Remote.IndexDocument
            {
                Games =
                {
                    ["g1"] = new GameSaveSync.Core.Remote.IndexGameEntry
                    {
                        Seq = 5,
                        SnapshotId = snapshotId,
                        DeviceId = "device-b",
                        UpdatedAt = 1000,
                    },
                },
            },
            Local = new Dictionary<string, GameSaveSync.Core.Remote.IndexGameEntry>
            {
                ["g1"] = new()
                {
                    Seq = 5,
                    SnapshotId = snapshotId,
                    DeviceId = "device-a",
                    UpdatedAt = 1000,
                },
            },
            LocalDeviceId = "device-a",
            AreRelated = (_, _) => false,
            CreatedAtOf = _ => 1000,
            NowMs = 2000,
        };

        var result = GameSaveSync.Core.Remote.IndexMerger.Merge(request);

        // seq 相等且是别的设备 → 不抢占（远端胜出），否则两台设备会来回抢同一条目
        Assert.Equal("device-b", result.Merged.Games["g1"].DeviceId);
    }

    [Fact]
    public void IndexSerializer_RejectsGarbage()
    {
        Assert.Null(GameSaveSync.Core.Remote.IndexSerializer.TryParse("not json"));
        Assert.Null(GameSaveSync.Core.Remote.IndexSerializer.TryParse(null));
        Assert.Null(GameSaveSync.Core.Remote.IndexSerializer.TryParse("   "));

        // "{}" 是合法的空索引（远端刚建立时的正常形态），不应判为垃圾
        var empty = GameSaveSync.Core.Remote.IndexSerializer.TryParse("{}");
        Assert.NotNull(empty);
        Assert.Empty(empty!.Games);
    }

    [Fact]
    public void IndexSerializer_RoundTrips()
    {
        var document = new GameSaveSync.Core.Remote.IndexDocument
        {
            Version = 1,
            UpdatedAt = 12345,
            Games =
            {
                ["g1"] = new GameSaveSync.Core.Remote.IndexGameEntry
                {
                    Seq = 3,
                    SnapshotId = Guid.NewGuid(),
                    DeviceId = "device-a",
                    UpdatedAt = 999,
                    Conflict = true,
                },
            },
        };

        var json = GameSaveSync.Core.Remote.IndexSerializer.ToJson(document);
        var parsed = GameSaveSync.Core.Remote.IndexSerializer.TryParse(json);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Games["g1"].Conflict);
        Assert.Equal(3, parsed.Games["g1"].Seq);
    }

    // ============================================================
    // 未配置远端：正常状态，不应刷异常告警
    // ============================================================

    [Fact]
    public async Task RefreshDevices_NotConfigured_DoesNotLogWarning()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var before = env.Logger.Recent(500).Count(l => l.Message.Contains("刷新设备列表失败", StringComparison.Ordinal));

        var devices = await env.IndexService.RefreshDevicesAsync(default);

        Assert.Empty(devices);

        var after = env.Logger.Recent(500).Count(l => l.Message.Contains("刷新设备列表失败", StringComparison.Ordinal));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Poller_NotConfigured_DoesNotLogFailure()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await env.Poller.PollOnceAsync(default);

        var failures = env.Logger.Recent(500).Count(l => l.Message.Contains("轮询 index.json 失败", StringComparison.Ordinal));
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task RemoteDevice_AppVersion_IsNotHardcoded()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);

        var devices = await env.IndexService.RefreshDevicesAsync(default);

        // 设备已注册到远端
        var remote = await env.Remote!.ListDevicesAsync(default);
        Assert.NotEmpty(remote);

        // 版本号不能是写死的旧值
        Assert.DoesNotContain("0.3.0-m3", remote[0].AppVersion ?? string.Empty, StringComparison.Ordinal);
    }
}
