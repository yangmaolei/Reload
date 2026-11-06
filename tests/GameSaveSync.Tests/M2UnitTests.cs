using GameSaveSync.Core.Detection;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

public sealed class M2UnitTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Theory]
    [InlineData("game.exe", @"C:\Games\Demo\bin\game.exe", true)]
    [InlineData("GAME.EXE", @"C:\games\demo\bin\game.exe", true)]
    [InlineData("game.exe", @"C:\Other\game.exe", false)]              // 不在安装目录下
    [InlineData("launcher.exe", @"C:\Games\Demo\launcher.exe", false)] // exe 名不匹配
    public void ProcessMatcher_RequiresNameAndInstallDir(string exeName, string processPath, bool expected)
    {
        var game = new Game
        {
            Name = "Demo",
            InstallDir = @"C:\Games\Demo",
            ExeNames = { "game" },
        };

        var process = new GameProcessMatcher.RunningProcess(1234, exeName, processPath);

        Assert.Equal(expected, GameProcessMatcher.Matches(game, process));
    }

    [Fact]
    public void ProcessMatcher_WithoutExeNamesNeverMatches()
    {
        var game = new Game { Name = "Demo" };
        var process = new GameProcessMatcher.RunningProcess(1, "game.exe", @"C:\Games\game.exe");

        Assert.False(GameProcessMatcher.Matches(game, process));
    }

    [Fact]
    public void Backoff_GrowsExponentiallyAndCapsAtFiveMinutes()
    {
        var noJitter = new Random(1);

        var first = OutboxBackoff.Compute(1, noJitter);
        var second = OutboxBackoff.Compute(2, noJitter);
        var third = OutboxBackoff.Compute(3, noJitter);
        var capped = OutboxBackoff.Compute(30, noJitter);

        Assert.InRange(first.TotalSeconds, 0.79, 1.21);
        Assert.InRange(second.TotalSeconds, 1.58, 2.42);
        Assert.InRange(third.TotalSeconds, 3.16, 4.84);
        // 封顶 5 分钟后仍带 ±20% 抖动
        Assert.InRange(capped.TotalSeconds, 239, 361);
    }

    [Fact]
    public void Backoff_JitterStaysWithinTwentyPercent()
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var expected = Math.Min(1 << (attempt - 1), 300);
            for (var i = 0; i < 20; i++)
            {
                var actual = OutboxBackoff.Compute(attempt).TotalSeconds;
                Assert.InRange(actual, expected * 0.79, expected * 1.21);
            }
        }
    }

    [Fact]
    public async Task Outbox_EnqueueIsDedupedAndLeaseMovesToInflight()
    {
        var first = await _env.Outbox.EnqueueAsync(
            OutboxOperations.PutObject, OutboxPayloads.ForObject("abc"), "obj:abc", false, CancellationToken.None);
        var second = await _env.Outbox.EnqueueAsync(
            OutboxOperations.PutObject, OutboxPayloads.ForObject("abc"), "obj:abc", false, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, await _env.Outbox.CountPendingAsync(CancellationToken.None));

        var leased = await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs, CancellationToken.None);
        Assert.NotNull(leased);
        Assert.Equal(OutboxStates.Inflight, leased!.State);
        Assert.Equal(1, leased.Attempts);

        // inflight 的项不会被再次租到
        Assert.Null(await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs, CancellationToken.None));
    }

    [Fact]
    public async Task Outbox_RequeueInflightRestoresPendingAfterCrash()
    {
        await _env.Outbox.EnqueueAsync(
            OutboxOperations.PutObject, OutboxPayloads.ForObject("abc"), "obj:abc", false, CancellationToken.None);
        await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs, CancellationToken.None);   // 模拟"处理中"

        var requeued = await _env.Outbox.RequeueInflightAsync(CancellationToken.None);

        Assert.Equal(1, requeued);
        Assert.Equal(1, await _env.Outbox.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Outbox_FailSetsBackoffAndPermanentState()
    {
        await _env.Outbox.EnqueueAsync(
            OutboxOperations.PutObject, OutboxPayloads.ForObject("abc"), "obj:abc", false, CancellationToken.None);
        var item = await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs, CancellationToken.None);

        await _env.Outbox.FailAsync(item!.Id, "server down", _env.Clock.UtcNowMs + 60_000, permanent: false, CancellationToken.None);

        // 还没到重试时间
        Assert.Null(await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs, CancellationToken.None));
        // 时间到了就能再取到
        Assert.NotNull(await _env.Outbox.LeaseDueAsync(_env.Clock.UtcNowMs + 61_000, CancellationToken.None));

        var recent = await _env.Outbox.ListRecentAsync(10, CancellationToken.None);
        Assert.Equal("server down", recent[0].LastError);
    }

    [Fact]
    public async Task StabilityGate_ReturnsFalseForContinuouslyGrowingFile()
    {
        var dir = _env.CreateSaveDir("Saved Games/Stability", ("a.sav", TestEnvironment.Bytes(1024)));
        var game = await _env.AddGameAsync("Stability", default, dir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var writer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var stream = new FileStream(Path.Combine(dir, "a.sav"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(new byte[256]);
                await Task.Delay(20);
            }
        });

        var stable = await _env.StabilityGate.WaitForStableAsync(game, CancellationToken.None);

        cts.Cancel();
        await writer;

        Assert.False(stable, "文件持续增长时必须判定为不稳定");
    }

    [Fact]
    public async Task StabilityGate_ReturnsTrueForQuietDirectory()
    {
        var dir = _env.CreateSaveDir("Saved Games/Quiet", ("a.sav", TestEnvironment.Bytes(1024)));
        var game = await _env.AddGameAsync("Quiet", default, dir);

        Assert.True(await _env.StabilityGate.WaitForStableAsync(game, CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_SkipIfUnchangedProducesNoSnapshot()
    {
        var dir = _env.CreateSaveDir("Saved Games/Unchanged", ("a.sav", TestEnvironment.Bytes(512, seed: 3)));
        var game = await _env.AddGameAsync("Unchanged", default, dir);

        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True(first.Created);

        _env.SnapshotService.SkipIfUnchangedWithParent = true;
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.PostChange, null, CancellationToken.None);

        Assert.False(second.Created);
        Assert.Contains("完全一致", second.SkipReason);
        Assert.Equal(1, await _env.Snapshots.CountAsync(game.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Fingerprints_ArePersistedAndUsedForFastPath()
    {
        var dir = _env.CreateSaveDir("Saved Games/Fingerprint", ("a.sav", TestEnvironment.Bytes(2048, seed: 5)));
        var game = await _env.AddGameAsync("Fingerprint", default, dir);

        await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var fingerprints = await _env.Fingerprints.LoadAsync(game.Id, CancellationToken.None);

        Assert.Single(fingerprints);
        var record = fingerprints.Values.Single();
        Assert.Equal(TestEnvironment.HashFile(Path.Combine(dir, "a.sav")), record.Hash);

        // 内容没变、mtime 没变 → 第二次快照走 L2 快路径，不再新增对象
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.Equal(0, second.NewObjects);
        Assert.Equal(1, second.ReusedObjects);
    }

    [Fact]
    public async Task Retention_KeepsManualSnapshotsAndDropsOnlyOldOnes()
    {
        var dir = _env.CreateSaveDir("Saved Games/Gfs", ("a.sav", TestEnvironment.Bytes(256, seed: 1)));
        var game = await _env.AddGameAsync("Gfs", default, dir);
        game.Retention = new RetentionPolicy { KeepLast = 20, Daily = 7, Weekly = 4 };
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(8));
        var created = new List<Guid>();
        var manualIds = new HashSet<Guid>();

        for (var i = 0; i < 40; i++)
        {
            _env.Clock.Set(start.AddDays(i));
            File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(256, seed: i));

            var trigger = i == 5 ? SnapshotTrigger.Manual : SnapshotTrigger.PostChange;
            var captured = await _env.SnapshotService.CreateAsync(game.Id, trigger, $"snap{i}", CancellationToken.None);
            created.Add(captured.SnapshotId!.Value);
            if (trigger == SnapshotTrigger.Manual)
            {
                manualIds.Add(captured.SnapshotId!.Value);
            }
        }

        _env.Clock.Set(start.AddDays(40));
        var plan = await _env.Retention.PreviewAsync(game.Id, CancellationToken.None);

        Assert.False(plan.IsEmpty);
        Assert.True(plan.BytesFreed > 0);

        var result = await _env.Retention.ApplyAsync(plan, CancellationToken.None);

        var remaining = await _env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);
        var remainingIds = remaining.Select(s => s.Id).ToHashSet();

        Assert.True(result.PrunedSnapshots > 0);
        Assert.True(remaining.Count < 40);
        Assert.True(remaining.Count >= 20, $"保留数量不应少于 keepLast，实际 {remaining.Count}");
        Assert.Contains(created[^1], remainingIds);                     // 最新的一定在
        Assert.All(manualIds, id => Assert.Contains(id, remainingIds));  // 手动的一个都不能删
        Assert.All(remaining, s => Assert.Equal(LocalSnapshotState.Present, s.LocalState));

        // 被清理的快照必须都是 28 天以前的老快照
        var pruned = created.Where(id => !remainingIds.Contains(id)).ToList();
        Assert.NotEmpty(pruned);
    }

    [Fact]
    public async Task Retention_DoesNotDeleteObjectsStillReferencedByKeptSnapshots()
    {
        var dir = _env.CreateSaveDir("Saved Games/RefCount",
            ("a.sav", TestEnvironment.Bytes(128, seed: 1)),
            ("b.sav", TestEnvironment.Bytes(128, seed: 2)));
        var game = await _env.AddGameAsync("RefCount", default, dir);
        game.Retention = new RetentionPolicy { KeepLast = 1, Daily = 0, Weekly = 0 };
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        _env.Clock.Set(DateTimeOffset.Now.AddDays(-40));
        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.PostChange, null, CancellationToken.None);

        _env.Clock.Set(DateTimeOffset.Now);
        File.WriteAllBytes(Path.Combine(dir, "b.sav"), TestEnvironment.Bytes(128, seed: 9));
        await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.PostChange, null, CancellationToken.None);

        var sharedHash = TestEnvironment.HashFile(Path.Combine(dir, "a.sav"));
        var plan = await _env.Retention.PreviewAsync(game.Id, CancellationToken.None);
        await _env.Retention.ApplyAsync(plan, CancellationToken.None);

        // a.sav 在两个快照里内容相同 → 被保留的快照仍引用它，不能删
        Assert.True(_env.Store.Contains(sharedHash), "仍被引用的对象被误删");
        var pruned = await _env.Snapshots.GetAsync(first.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(LocalSnapshotState.Pruned, pruned!.LocalState);
    }
}
