using GameSaveSync.Core.Storage;
using GameSaveSync.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Tests;

/// <summary>
/// 缺陷回归用例（M5 全量测试批次）。
/// 每个用例对应一个已定位的真实缺陷：先复现，再由修复让它转绿。
/// </summary>
public class M5RegressionTests
{
    // ============================================================
    // 1. Outbox：forceDue 必须能唤醒已 failed 的项
    //    缺陷：ON CONFLICT DO UPDATE 只重置 next_retry_at，不重置 state，
    //          而 LeaseDueAsync 只捞 state='pending' → 用户点"重试上传"永远没反应。
    // ============================================================

    [Fact]
    public async Task Outbox_ForceDue_ResurrectsFailedItem()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var store = env.Outbox;

        await store.EnqueueAsync("put_object", "{\"hash\":\"abc\"}", "obj:abc", forceDue: false, default);
        var leased = await store.LeaseDueAsync(env.Clock.UtcNowMs, default);
        Assert.NotNull(leased);

        // 标记为永久失败
        await store.FailAsync(leased!.Id, "永久失败", 0, permanent: true, default);
        Assert.Equal(1, await store.CountFailedAsync(default));

        // 用户点"重试" → forceDue=true，应该把它拉回 pending 并可被租到
        var enqueued = await store.EnqueueAsync(
            "put_object", "{\"hash\":\"abc\"}", "obj:abc", forceDue: true, default);
        Assert.True(enqueued, "forceDue 应视为一次有效入队（至少要重置状态）");

        var again = await store.LeaseDueAsync(env.Clock.UtcNowMs, default);
        Assert.NotNull(again);
        Assert.Equal("obj:abc", again!.DedupeKey);
    }

    [Fact]
    public async Task Outbox_ResetFailed_ClearsAttemptsAndState()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var store = env.Outbox;

        await store.EnqueueAsync("put_object", "{}", "obj:x", forceDue: false, default);
        var leased = await store.LeaseDueAsync(env.Clock.UtcNowMs, default);
        await store.FailAsync(leased!.Id, "boom", 0, permanent: true, default);

        Assert.Equal(1, await store.CountFailedAsync(default));

        await store.ResetFailedAsync(default);

        Assert.Equal(0, await store.CountFailedAsync(default));
        var recent = await store.ListRecentAsync(10, default);
        Assert.Equal("pending", recent[0].State);
        Assert.Equal(0, recent[0].Attempts);
    }

    [Fact]
    public async Task Outbox_LeaseDue_ReturnsNullWhenEmpty()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        Assert.Null(await env.Outbox.LeaseDueAsync(env.Clock.UtcNowMs, default));
    }

    [Fact]
    public async Task Outbox_RequeueInflight_ResetsInflightToPending()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var store = env.Outbox;

        await store.EnqueueAsync("put_object", "{}", "obj:1", forceDue: false, default);
        var leased = await store.LeaseDueAsync(env.Clock.UtcNowMs, default);
        Assert.NotNull(leased);
        Assert.Equal("inflight", (await store.ListRecentAsync(1, default))[0].State);

        var count = await store.RequeueInflightAsync(default);

        Assert.Equal(1, count);
        Assert.Equal("pending", (await store.ListRecentAsync(1, default))[0].State);
    }

    [Fact]
    public async Task Outbox_Enqueue_RejectsBlankOp()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            env.Outbox.EnqueueAsync("  ", "{}", "k", forceDue: false, default));
    }

    [Fact]
    public async Task Outbox_GetLastError_ReturnsNullWhenNoError()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        Assert.Null(await env.Outbox.GetLastErrorAsync(default));
    }

    // ============================================================
    // 2. OutboxProcessor：取消时必须用独立 token 写回状态
    //    缺陷：catch(OperationCanceledException) 里用已取消的 ct 调 FailAsync，
    //          FailAsync 内部会立刻抛，导致项永远卡在 inflight。
    // ============================================================

    [Fact]
    public async Task OutboxProcessor_CancelledOperation_RequeuesItemInsteadOfStuckInflight()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await env.Outbox.EnqueueAsync("put_object", "{\"hash\":\"aa\"}", "obj:aa", forceDue: false, default);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 处理器应感知取消
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            env.OutboxProcessor.ProcessDueAsync(cts.Token));

        // 关键断言：项不能停留在 inflight
        var recent = await env.Outbox.ListRecentAsync(10, default);
        Assert.NotEmpty(recent);
        Assert.NotEqual("inflight", recent[0].State);
    }

    // ============================================================
    // 3. ConflictService：设备 ID 短于 8 字符不能崩
    //    缺陷：d.Id[..8] 在 Id 长度 < 8 时抛 ArgumentOutOfRangeException。
    // ============================================================

    [Fact]
    public async Task ConflictService_ShortDeviceId_DoesNotCrash()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);

        // 造一个设备 ID 很短的远端设备记录
        await env.Remote!.PutDeviceAsync(
            new GameSaveSync.Core.Remote.DeviceDocument { Id = "x", Name = "", LastSeenAt = 1 },
            default);

        var dir = env.CreateSaveDir("Saved Games/Conflict", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("冲突游戏", default, dir);

        var snap1 = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        Assert.NotNull(snap1.SnapshotId);

        await env.IndexState.ReplaceRemoteAsync(
            new Dictionary<string, GameSaveSync.Core.Remote.IndexGameEntry>
            {
                [game.Id.ToString("D")] = new()
                {
                    Seq = 1,
                    SnapshotId = snap1.SnapshotId!.Value,
                    DeviceId = "x",
                    Conflict = true,
                    UpdatedAt = env.Clock.UtcNowMs,
                },
            },
            env.Clock.UtcNowMs,
            default);

        // 不应抛异常（修复前 d.Id[..8] 会抛 ArgumentOutOfRangeException）
        var view = await env.Conflicts.GetAsync(game.Id, default);
        Assert.True(view is null || view.Left is not null);
    }

    // ============================================================
    // 4. ObjectRepository：引用计数回收必须限定在本次涉及的 hash
    //    缺陷：SELECT hash FROM objects WHERE refcount <= 0 是全库查询，
    //          会把别的游戏遗留的 refcount<=0 对象一起回收（删本地 CAS + 删远端）。
    // ============================================================

    [Fact]
    public async Task ObjectRepository_DecrementReferences_OnlyReturnsRequestedHashes()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        // 另一个游戏的孤儿对象（refcount 已经 <= 0）
        await env.Objects.RegisterManyAsync(new[] { ("orphan", 10L) }, default);
        await env.Objects.DecrementReferencesAsync(new[] { "orphan" }, default);

        // 本次要释放的 hash
        await env.Objects.RegisterManyAsync(new[] { ("mine", 20L) }, default);
        await env.Objects.DecrementReferencesAsync(new[] { "mine" }, default);

        var unreferenced = await env.Objects.DecrementReferencesAsync(new[] { "mine" }, default);

        Assert.DoesNotContain("orphan", unreferenced);
    }

    [Fact]
    public async Task ObjectRepository_RegisterSameHashTwice_ReferenceCountIsSymmetric()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        // 同一快照里两个文件内容相同 → 注册端按去重计数（+1）
        await env.Objects.RegisterManyAsync(new[] { ("dup", 5L), ("dup", 5L) }, default);

        // 释放端按去重减 1 → 恰好归零，应被判定为可回收
        var released = await env.Objects.DecrementReferencesAsync(new[] { "dup" }, default);
        Assert.True(released.Contains("dup"), "引用计数应对称归零并可回收");
    }

    [Fact]
    public async Task ObjectRepository_RegisterAcrossTwoSnapshots_NeedsTwoReleases()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        // 两个快照都引用同一对象 → refcount = 2
        await env.Objects.RegisterManyAsync(new[] { ("shared", 5L) }, default);
        await env.Objects.RegisterManyAsync(new[] { ("shared", 5L) }, default);

        // 删掉第一个快照 → 只减 1，仍被引用，不能回收
        var first = await env.Objects.DecrementReferencesAsync(new[] { "shared" }, default);
        Assert.False(first.Contains("shared"), "仍被另一个快照引用时不能回收");

        // 删掉第二个快照 → 归零，可以回收
        var second = await env.Objects.DecrementReferencesAsync(new[] { "shared" }, default);
        Assert.True(second.Contains("shared"), "最后一个引用释放后应可回收");
    }

    // ============================================================
    // 5. 多存档根同名文件：snapshot_files 主键缺 root_index
    //    缺陷：PRIMARY KEY(snapshot_id, rel_path_key)，而 rel_path_key 不含 root index，
    //          两个根下同名文件 → 主键冲突 → 整个快照失败。
    // ============================================================

    [Fact]
    public async Task Snapshot_TwoRootsWithSameFileName_Succeeds()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var rootA = env.CreateSaveDir("Saved Games/GameA", ("save.dat", TestEnvironment.Bytes(32, 1)));
        var rootB = env.CreateSaveDir("Saved Games/GameB", ("save.dat", TestEnvironment.Bytes(32, 2)));

        var game = await env.AddGameAsync("多根游戏", default, rootA, rootB);

        var result = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);

        Assert.NotNull(result.SnapshotId);
        Assert.Equal(2, result.FileCount);
    }

    // ============================================================
    // 6. RestoreService：恢复时必须遵守全局排除
    //    缺陷：快照阶段跳过全局排除的文件，恢复阶段却把它们当成"多余文件"删除。
    // ============================================================

    [Fact]
    public async Task Restore_GlobalExcludedFile_IsNotDeleted()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir(
            "Saved Games/Excl",
            ("keep.dat", TestEnvironment.Bytes(16, 1)),
            ("debug.log", TestEnvironment.Bytes(16, 2)));

        var game = await env.AddGameAsync("排除游戏", default, dir);

        // 首次快照：没有全局排除，两个文件都进快照
        var first = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        Assert.NotNull(first.SnapshotId);

        // 开启全局排除 *.log，再拍一次
        await env.Settings.SetAsync(
            GameSaveSync.Core.Snapshots.SnapshotService.GlobalExcludeKey, "*.log", default);
        var second = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        Assert.NotNull(second.SnapshotId);

        // 恢复第二个快照：debug.log 不在清单里，但也不该被删（它是全局排除项）
        var plan = await env.RestoreService.PlanAsync(second.SnapshotId!.Value, default);

        Assert.DoesNotContain(plan.Operations, o =>
            o.Kind == GameSaveSync.Core.Restore.RestoreOperationKind.Delete &&
            o.RelPath.EndsWith("debug.log", StringComparison.OrdinalIgnoreCase));

        await env.RestoreService.ExecuteAsync(plan, null, default);
        Assert.True(File.Exists(Path.Combine(dir, "debug.log")), "全局排除的文件不能在恢复时被删掉");
    }

    // ============================================================
    // 7. WebDavClient.PutAsync 必须检查响应状态
    //    缺陷：只查 401/403，服务端 500/503 也当成功。
    // ============================================================

    [Fact]
    public async Task WebDavClient_PutAsync_ServerError_Throws()
    {
        await using var server = new InMemoryWebDavServer { FailPutStatus = 500 };

        var client = new GameSaveSync.WebDav.WebDavClient(new GameSaveSync.WebDav.WebDavOptions
        {
            BaseUrl = server.BaseUrl,
            RemoteRoot = "GameSaveSync",
            Username = "tester",
            Password = "secret",
            UseProxy = false,
        });

        // 服务端返回 500 → 必须抛异常，不能静默当成功
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.PutAsync("some/file.bin", new byte[] { 1, 2, 3 }, default));
    }

    // ============================================================
    // 8. 索引写入：服务端不返 ETag 时不能无条件覆盖（丢更新）
    //    缺陷：canUseIfMatch=false 时直接无条件 PUT，注释说"靠写前重读 + 内容哈希比较"
    //          但代码里根本没有内容哈希比较。
    // ============================================================

    [Fact]
    public async Task RemoteIndex_PublishWithoutETag_DoesNotLoseOtherDeviceEntries()
    {
        await using var server = new InMemoryWebDavServer();
        await using var envA = TestEnvironment.Create(withWebDav: true, sharedServer: server);
        await using var envB = TestEnvironment.CreateSecondDevice(server);

        // B 先发布一个游戏
        var dirB = envB.CreateSaveDir("Saved Games/B", ("a.dat", TestEnvironment.Bytes(16)));
        var gameB = await envB.AddGameAsync("B的游戏", default, dirB);
        var snapB = await envB.SnapshotService.CreateAsync(
            gameB.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        await envB.IndexService.PublishAsync(gameB.Id, snapB.SnapshotId!.Value, default);

        // A 再发布另一个游戏（服务端不返 ETag 的降级场景）
        var dirA = envA.CreateSaveDir("Saved Games/A", ("a.dat", TestEnvironment.Bytes(16)));
        var gameA = await envA.AddGameAsync("A的游戏", default, dirA);
        var snapA = await envA.SnapshotService.CreateAsync(
            gameA.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        await envA.IndexService.PublishAsync(gameA.Id, snapA.SnapshotId!.Value, default);

        // 远端索引应同时包含两个游戏的条目（索引按 gameId 存）
        var json = server.GetText("GameSaveSync/index.json");
        Assert.NotNull(json);
        Assert.Contains(gameA.Id.ToString("D"), json!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(gameB.Id.ToString("D"), json!, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // 9. PcGamingWikiProvider：formatversion=2 的 wikitext 是字符串
    //    缺陷：代码按旧版 {"*": "..."} 解析，formatversion=2 下真实响应是纯字符串，
    //          TryGetProperty 抛异常被外层吞掉 → 在线查询永远返回空。
    // ============================================================

    [Fact]
    public async Task Provider_FormatVersion2_PlainStringWikitext_IsParsed()
    {
        var handler = new StringHandler(url => url.Contains("list=search")
            ? """{"query":{"search":[{"title":"Cyberpunk 2077"}]}}"""
            : """{"parse":{"wikitext":"{{Game data/saves|Windows|{{p|userprofile\\\\Saved Games\\\\Foo}}}}"}}""");

        using var provider = new GameSaveSync.Core.Online.PcGamingWikiProvider(
            new HttpClient(handler) { BaseAddress = new Uri("https://test/") });

        var result = await provider.QueryAsync("Cyberpunk 2077", default);

        Assert.Single(result);
        Assert.Equal("%USERPROFILE%\\Saved Games\\Foo", result[0].PathTemplate);
    }

    // ============================================================
    // 10. MiniYaml：冒号后无空格也应当能解析（真实 yaml 常不守规矩）
    // ============================================================

    [Fact]
    public void MiniYaml_KeyWithoutSpaceAfterColon_IsParsed()
    {
        var yaml = """
            version: 1
            rules:
            - id: r1
              namePattern:Old*
              engine:Unity
              roots:
              - path:%USERPROFILE%\Old
            """;

        var doc = GameSaveSync.Core.Locator.MiniYaml.ParseMapping(yaml);

        Assert.NotNull(doc);
        Assert.True(doc!.ContainsKey("rules"), "冒号后无空格的键值不应导致整块被丢弃");
    }

    // ============================================================
    // 11. ProbeService：探测期间被删除的文件也要报告
    // ============================================================

    [Fact]
    public async Task Probe_DeletedFile_IsReportedAsChange()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/ProbeDel", ("a.dat", TestEnvironment.Bytes(8)));

        var probe = new GameSaveSync.Core.Probe.ProbeService(
            env.FileSystem, env.Clock, env.Logger);

        var roots = new GameSaveSync.Core.Probe.ProbeRoots(new[] { dir });
        var session = await probe.StartAsync(roots, null, default);
        File.Delete(Path.Combine(dir, "a.dat"));
        var result = await probe.CompleteAsync(session, null, default);

        Assert.Equal(1, result.ChangedFiles);
    }

    // ============================================================
    // 12. 设备 ID 并发唯一性
    // ============================================================

    [Fact]
    public async Task Settings_GetDeviceId_IsUniqueUnderConcurrency()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            ids.Add(await env.Settings.GetDeviceIdAsync(default));
        })));

        Assert.Single(ids.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    // ============================================================
    // 13. FileSystemObjectStore：桶目录创建失败后应能重试
    //    缺陷：_ensuredBuckets.Add 在 CreateDirectory 之前，
    //          创建失败后该桶被永久标记为"已建" → 该前缀对象永远写不进去。
    // ============================================================

    [Fact]
    public async Task ObjectStore_BucketCreationFailure_CanBeRetried()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var hash = TestEnvironment.HashFileWith(TestEnvironment.Bytes(32, 9));
        var bucketDir = env.Paths.BucketDirectory(hash);

        // 让桶目录无法被创建：把桶路径占成一个普通文件
        Directory.CreateDirectory(Path.GetDirectoryName(bucketDir)!);
        if (Directory.Exists(bucketDir))
        {
            Directory.Delete(bucketDir, recursive: true);
        }

        File.WriteAllText(bucketDir, "not-a-directory");

        // 新建一个"从未标记过桶"的对象库实例：首次写入必须失败
        var store = new FileSystemObjectStore(
            env.Paths, env.FileSystem, GameSaveSync.Core.Hashing.Blake3HashService.Instance);

        await using (var bad = new MemoryStream(TestEnvironment.Bytes(32, 9)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.PutAsync(hash, bad, default));
        }

        // 关键：桶不能被永久标记为"已建"。环境修好后必须能写入成功。
        File.Delete(bucketDir);

        await using (var good = new MemoryStream(TestEnvironment.Bytes(32, 9)))
        {
            await store.PutAsync(hash, good, default);
        }

        Assert.True(store.Contains(hash));
    }

    // ============================================================
    // 14. 快照清单与索引的 gameId 必须一致
    // ============================================================

    [Fact]
    public async Task Importer_ManifestGameIdMismatch_Throws()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);

        var dir = env.CreateSaveDir("Saved Games/Src", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("源游戏", default, dir);
        var snap = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        Assert.NotNull(snap.SnapshotId);

        // 上传快照到远端
        await env.UploadService.UploadSnapshotAsync(snap.SnapshotId!.Value, default);
        await env.OutboxProcessor.ProcessDueAsync(default);

        // 找到远端清单文件并篡改其中的 gameId（模拟错配/被篡改的远端）
        var prefix = $"GameSaveSync/games/{game.Id:D}/snapshots/";
        var manifestKey = env.Server!.KeysWithPrefix(prefix).Single();
        var manifestJson = env.Server.GetText(manifestKey);
        Assert.NotNull(manifestJson);

        var manifest = GameSaveSync.Core.Snapshots.SnapshotManifestSerializer.FromJson(manifestJson!);
        manifest.GameId = Guid.NewGuid();
        env.Server.Overwrite(
            manifestKey,
            System.Text.Encoding.UTF8.GetBytes(
                GameSaveSync.Core.Snapshots.SnapshotManifestSerializer.ToJson(manifest)));

        // 用另一台设备导入 → 应拒绝（清单指向的游戏与索引不一致）
        await using var envB = TestEnvironment.CreateSecondDevice(env.Server!);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            envB.Importer.ImportAsync(game.Id, snap.SnapshotId!.Value, default));
    }

    // ============================================================
    // 15. 路径穿越：远端清单里的 RelPath 不得写到存档根之外
    // ============================================================

    [Fact]
    public async Task Restore_RelativePathEscape_IsRejected()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Escape", ("ok.dat", TestEnvironment.Bytes(8)));
        var game = await env.AddGameAsync("穿越游戏", default, dir);

        var snap = await env.SnapshotService.CreateAsync(
            game.Id, GameSaveSync.Core.Models.SnapshotTrigger.Manual, null, default);
        Assert.NotNull(snap.SnapshotId);

        // 直接往库里塞一条带 ../ 的恶意文件记录（模拟被篡改的远端清单）
        var evilHash = TestEnvironment.HashFileWith(TestEnvironment.Bytes(3));
        await using (var connection = new SqliteConnection($"Data Source={env.Paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO snapshot_files(snapshot_id, rel_path, rel_path_key, hash, size, mtime, attrs, root_index)
                VALUES($sid, $rel, $key, $hash, 3, 0, 0, 0)
                """;
            command.Parameters.AddWithValue("$sid", snap.SnapshotId.Value.ToString("D"));
            command.Parameters.AddWithValue("$rel", "..\\..\\evil.dll");
            command.Parameters.AddWithValue("$key", "..\\..\\evil.dll");
            command.Parameters.AddWithValue("$hash", evilHash);
            await command.ExecuteNonQueryAsync();
        }

        var evilOutside = Path.GetFullPath(Path.Combine(dir, "..", "..", "evil.dll"));
        if (File.Exists(evilOutside))
        {
            File.Delete(evilOutside);
        }

        // 生成计划时就应该拒绝越界路径
        var plan = await env.RestoreService.PlanAsync(snap.SnapshotId!.Value, default);

        Assert.DoesNotContain(plan.Operations, o =>
            o.AbsolutePath.Contains("evil.dll", StringComparison.OrdinalIgnoreCase) &&
            !o.AbsolutePath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

        if (plan.CanExecute && plan.Operations.Count > 0)
        {
            await env.RestoreService.ExecuteAsync(plan, null, default);
        }

        Assert.False(File.Exists(evilOutside), "越界路径绝不能写到存档根之外");
    }

    private sealed class StringHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;

        public StringHandler(Func<string, string> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = _respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
