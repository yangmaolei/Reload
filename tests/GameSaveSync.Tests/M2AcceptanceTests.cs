using System.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;
using Xunit.Abstractions;

namespace GameSaveSync.Tests;

/// <summary>
/// M2 交付验收 8 条。能自动化的全部自动化；
/// 只有"PresentMon 帧时间"这一条在当前环境无法执行（没有游戏、没有 PresentMon），
/// 用"后台 IO 路径确实降优先级 + 节流参数生效"作为可验证的替代证据。
/// </summary>
public sealed class M2AcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public M2AcceptanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task B1_GameExit_ProducesExitSnapshotWithinFiveSecondsAndUploads()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/B1", ("save.sav", TestEnvironment.Bytes(1024, seed: 1)));
        var game = await ConfigureGameAsync(env, "B1", dir);

        await env.Scheduler.StartAsync(CancellationToken.None);
        env.ProcessWatcher.RaiseStarted(game);
        await WaitAsync(async () => await CountAsync(env, game.Id, SnapshotTrigger.PreLaunch) >= 1, "pre_launch");

        // 游戏在退出时写档
        File.WriteAllBytes(Path.Combine(dir, "save.sav"), TestEnvironment.Bytes(2048, seed: 7));

        var stopwatch = Stopwatch.StartNew();
        env.ProcessWatcher.RaiseExited(game);

        var appeared = await WaitAsync(
            async () => await CountAsync(env, game.Id, SnapshotTrigger.Exit) >= 1,
            "exit 快照",
            TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        var exitSnapshot = (await env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None))
            .First(s => s.Trigger == SnapshotTrigger.Exit);

        await WaitAsync(
            async () => (await env.Snapshots.GetAsync(exitSnapshot.Id, CancellationToken.None))!.RemoteState
                        == RemoteSnapshotState.Uploaded,
            "上传完成");

        var uploaded = await env.Snapshots.GetAsync(exitSnapshot.Id, CancellationToken.None);

        _output.WriteLine($"[B1] exit 快照出现耗时 {stopwatch.ElapsedMilliseconds} ms，trigger={exitSnapshot.Trigger}，" +
                          $"文件数={exitSnapshot.FileCount}，remote_state={uploaded!.RemoteState}");

        Assert.True(appeared);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "必须 5 秒内出现 exit 快照");
        Assert.Equal(RemoteSnapshotState.Uploaded, uploaded.RemoteState);
        Assert.Equal(1, exitSnapshot.FileCount);
        Assert.Equal(2048, exitSnapshot.TotalSize);
    }

    [Fact]
    public async Task B2_IdleSnapshotUsesThrottledBackgroundIo()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("Saved Games/B2", ("save.sav", TestEnvironment.Bytes(4096, seed: 2)));
        var game = await ConfigureGameAsync(env, "B2", dir);

        await env.Scheduler.StartAsync(CancellationToken.None);
        env.ProcessWatcher.RaiseStarted(game);
        await WaitAsync(async () => await CountAsync(env, game.Id, SnapshotTrigger.PreLaunch) >= 1, "pre_launch");

        // 让内容变化，idle 快照才有东西可拍
        File.WriteAllBytes(Path.Combine(dir, "save.sav"), TestEnvironment.Bytes(4096, seed: 8));

        var created = await WaitAsync(
            async () => await CountAsync(env, game.Id, SnapshotTrigger.Idle) >= 1,
            "idle 快照",
            TimeSpan.FromSeconds(3));

        var idleCount = await CountAsync(env, game.Id, SnapshotTrigger.Idle);
        var ioEntriesDuringIdle = env.BackgroundIo.EnterCount;

        _output.WriteLine($"[B2] idle 快照={created}，数量={idleCount}，进入后台 IO 作用域次数={ioEntriesDuringIdle}，" +
                          $"节流参数=块 {env.SchedulerOptions.IdleIoOptions.ChunkSize} 字节 / 间隔 " +
                          $"{env.SchedulerOptions.IdleIoOptions.DelayBetweenChunksMs} ms");

        Assert.True(created);
        Assert.True(ioEntriesDuringIdle >= 1, "idle/后台快照必须走 IDLE 线程优先级作用域");
        Assert.Equal(1024 * 1024, env.SchedulerOptions.IdleIoOptions.ChunkSize);
        Assert.Equal(50, env.SchedulerOptions.IdleIoOptions.DelayBetweenChunksMs);

        // 单次后台快照的真实耗时（4 KB 存档，走节流参数与 IDLE 优先级）
        File.WriteAllBytes(Path.Combine(dir, "save.sav"), TestEnvironment.Bytes(4096, seed: 55));
        var ioBefore = env.BackgroundIo.EnterCount;
        var stopwatch = Stopwatch.StartNew();
        await env.Scheduler.CaptureAsync(game.Id, SnapshotTrigger.Idle, "计时", background: true, CancellationToken.None);
        stopwatch.Stop();
        _output.WriteLine($"[B2] 单次后台 idle 快照耗时 {stopwatch.ElapsedMilliseconds} ms" +
                          $"（4 KB，进入后台作用域 {env.BackgroundIo.EnterCount - ioBefore} 次）");

        // 手动快照不应降优先级
        var before = env.BackgroundIo.EnterCount;
        await env.Scheduler.CaptureAsync(game.Id, SnapshotTrigger.Manual, "manual", background: false, CancellationToken.None);
        Assert.Equal(before, env.BackgroundIo.EnterCount);

        // CPU 高时不做 idle 快照
        env.ResourceProbe.CpuUsage = 85;
        var idleBefore = await CountAsync(env, game.Id, SnapshotTrigger.Idle);
        await Task.Delay(900);
        Assert.Equal(idleBefore, await CountAsync(env, game.Id, SnapshotTrigger.Idle));

        _output.WriteLine("[B2] CPU 85% 期间未产生新的 idle 快照（前置条件生效）");
    }

    [Fact]
    public async Task B3_CrashDuringOutboxProcessing_NoPartialObjectsNoLostItemsNoDuplicates()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/B3", ("a.sav", TestEnvironment.Bytes(1024, seed: 3)));
        var game = await env.AddGameAsync("B3", default, dir);

        env.Server!.FailPutStatus = 503;
        var snapshot = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        await env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        var pendingBefore = await env.Outbox.CountPendingAsync(CancellationToken.None);
        Assert.True(pendingBefore >= 2, "对象与清单都应留在 outbox 里");

        // 模拟"执行到一半被强杀"：有一条处于 inflight
        // 失败后 next_retry_at 在退避区间里，用"未来时间"才能租到（模拟重启时重放）
        var inflight = await env.Outbox.LeaseDueAsync(env.Clock.UtcNowMs + 120_000, CancellationToken.None);
        Assert.NotNull(inflight);

        // 重启：新的处理器 + inflight 重新入队
        env.Server.FailPutStatus = 0;
        var requeued = await env.Outbox.RequeueInflightAsync(CancellationToken.None);
        var executor = new RemoteOperationExecutor(
            env.Remote!, env.Store, env.Objects, env.Snapshots, env.ManifestStore, env.Settings,
            env.IndexService, env.Logger);
        var processor = new OutboxProcessor(env.Outbox, executor, env.Clock, env.Logger);

        var logStart = env.Server.RequestLog.Count;
        var result = await processor.ProcessDueAsync(CancellationToken.None);
        var newRequests = env.Server.RequestLog.Skip(logStart).ToList();

        var objectPuts = newRequests
            .Where(r => r.StartsWith("PUT GameSaveSync/objects/", StringComparison.Ordinal))
            .ToList();
        var distinctObjects = objectPuts.Distinct(StringComparer.Ordinal).Count();

        var reloaded = await env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        // CAS 完整性：任何对象文件名都等于内容哈希
        var casFiles = Directory.EnumerateFiles(
                Path.Combine(env.DataRoot, "objects"), "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        foreach (var file in casFiles)
        {
            Assert.Equal(Path.GetFileName(file), TestEnvironment.HashFile(file));
        }

        _output.WriteLine($"[B3] inflight 重新入队 {requeued} 项；恢复后处理 {result.Processed} 项" +
                          $"（成功 {result.Succeeded} / 失败 {result.Failed}）；对象 PUT {objectPuts.Count} 次，" +
                          $"去重后 {distinctObjects} 个；快照状态={reloaded!.RemoteState}；CAS 对象 {casFiles.Count} 个全部哈希自洽");

        Assert.Equal(RemoteSnapshotState.Uploaded, reloaded.RemoteState);
        Assert.Equal(0, await env.Outbox.CountPendingAsync(CancellationToken.None));
        Assert.Equal(objectPuts.Count, distinctObjects);   // 同一个对象没有被重复上传
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task B4_OfflineFiveSnapshots_AllUploadInOrderWithoutDuplicates()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/B4", ("a.sav", TestEnvironment.Bytes(512, seed: 1)));
        var game = await env.AddGameAsync("B4", default, dir);

        env.Server!.FailPutStatus = 503;

        for (var i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(512, seed: i));
            var snapshot = await env.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.Exit, $"offline-{i}", CancellationToken.None);
            await env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        }

        var pending = await env.Snapshots.ListPendingAsync(CancellationToken.None);
        _output.WriteLine($"[B4] 断网期间产生 5 个快照，pending={pending.Count}，" +
                          $"outbox 待办={await env.Outbox.CountPendingAsync(CancellationToken.None)}");

        Assert.Equal(5, pending.Count);

        // 恢复网络。注意：真实场景要等退避，这里点"重试上传"= 手动提前重试
        env.Server.FailPutStatus = 0;
        var logStart = env.Server.RequestLog.Count;
        var results = await env.Uploads.UploadPendingAsync(CancellationToken.None);
        var newRequests = env.Server.RequestLog.Skip(logStart).ToList();

        var manifestPuts = newRequests
            .Where(r => r.StartsWith("PUT GameSaveSync/games/", StringComparison.Ordinal) &&
                        r.Contains("/snapshots/", StringComparison.Ordinal))
            .Select(StripTempSuffix)
            .ToList();

        var objectPuts = newRequests
            .Where(r => r.StartsWith("PUT GameSaveSync/objects/", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"[B4] 恢复后：成功 {results.Count(r => r.Success)} / {results.Count}；" +
                          $"清单 PUT {manifestPuts.Count} 次；对象 PUT {objectPuts.Count} 次，" +
                          $"去重后 {objectPuts.Distinct().Count()} 个");

        Assert.All(results, r => Assert.True(r.Success, r.Error));
        Assert.Equal(5, manifestPuts.Count);
        Assert.Equal(objectPuts.Count, objectPuts.Distinct(StringComparer.Ordinal).Count());

        // 顺序：清单按快照创建顺序上传（同一秒创建的快照靠 sid 区分）
        var expectedOrder = pending
            .OrderBy(s => s.CreatedAt)
            .Select(s => $"PUT GameSaveSync/games/{s.GameId:D}/snapshots/{SnapshotNames.ManifestFileName(s)}")
            .ToList();
        Assert.Equal(expectedOrder, manifestPuts);

        Assert.Empty(await env.Snapshots.ListPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task B5_TouchOnlyChangeDoesNotCreateNewSnapshot()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("Saved Games/B5", ("a.sav", TestEnvironment.Bytes(1024, seed: 4)));
        var game = await env.AddGameAsync("B5", default, dir);

        await env.Scheduler.StartAsync(CancellationToken.None);
        await env.Scheduler.CaptureAsync(game.Id, SnapshotTrigger.Manual, null, background: false, CancellationToken.None);
        var baseline = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);

        // 只改 mtime，内容不变
        File.SetLastWriteTimeUtc(Path.Combine(dir, "a.sav"), DateTime.UtcNow.AddHours(1));
        env.FileWatcher.RaiseChanged(game.Id);
        await Task.Delay(1200);

        var afterTouch = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);

        _output.WriteLine($"[B5] touch 后快照数：{baseline} → {afterTouch}（应完全不变）");
        Assert.Equal(baseline, afterTouch);

        // 证明流水线是活的：真改内容后立刻能拍出来
        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(1024, seed: 44));
        env.FileWatcher.RaiseChanged(game.Id);
        var created = await WaitAsync(
            async () => await env.Snapshots.CountAsync(game.Id, CancellationToken.None) > baseline,
            "内容变更后的快照");

        Assert.True(created, "内容变化后必须产生快照（说明上一步不是假阴性）");
        _output.WriteLine($"[B5] 内容变更后快照数：{await env.Snapshots.CountAsync(game.Id, CancellationToken.None)}");
    }

    [Fact]
    public async Task B6_TwentyRapidWritesProduceExactlyOneSnapshot()
    {
        // 去抖窗口刻意放大，避免 CI/负载抖动导致"写到一半就拍"
        await using var env = TestEnvironment.Create(withWebDav: false, schedulerOptions: new SnapshotSchedulerOptions
        {
            ExitDelay = TimeSpan.FromMilliseconds(120),
            DebounceDelay = TimeSpan.FromMilliseconds(600),
            IdleInterval = TimeSpan.FromMilliseconds(300),
            StartupCatchUpDelay = TimeSpan.Zero,
            RunRetentionAfterSnapshot = false,
        });
        var dir = env.CreateSaveDir("Saved Games/B6", ("slot1.sav", TestEnvironment.Bytes(1024, seed: 1)));
        var game = await env.AddGameAsync("B6", default, dir);

        await env.Scheduler.StartAsync(CancellationToken.None);
        await env.Scheduler.CaptureAsync(game.Id, SnapshotTrigger.Manual, null, background: false, CancellationToken.None);
        var baseline = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);

        // 模拟游戏自动存档：20 次快速写入，每次都会触发文件事件
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, "slot1.sav"), TestEnvironment.Bytes(1024, seed: 100 + i));
            env.FileWatcher.RaiseChanged(game.Id);
            await Task.Delay(40);
        }

        var created = await WaitAsync(
            async () => await env.Snapshots.CountAsync(game.Id, CancellationToken.None) > baseline,
            "写入停止后的快照",
            TimeSpan.FromSeconds(8));

        await Task.Delay(600);   // 再等一会儿，确认没有第二张

        var total = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);
        var latest = (await env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None))[0];

        _output.WriteLine($"[B6] 20 次快速写入后新增 {total - baseline} 个快照，" +
                          $"trigger={latest.Trigger}，内容哈希={TestEnvironment.HashFile(Path.Combine(dir, "slot1.sav"))[..12]}…");

        Assert.True(created);
        Assert.Equal(1, total - baseline);
        Assert.Equal(SnapshotTrigger.PostChange, latest.Trigger);
    }

    [Fact]
    public async Task B7_GfsRetentionKeepsRecentAndManualSnapshots()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/B7", ("a.sav", TestEnvironment.Bytes(512, seed: 1)));
        var game = await env.AddGameAsync("B7", default, dir);
        game.Retention = new RetentionPolicy { KeepLast = 20, Daily = 7, Weekly = 4 };
        await env.Games.UpsertAsync(game, CancellationToken.None);

        var start = new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.FromHours(8));
        var manualIds = new HashSet<Guid>();
        var allIds = new List<Guid>();

        for (var i = 0; i < 40; i++)
        {
            env.Clock.Set(start.AddHours(i * 12));            // 20 天，每 12 小时一个
            File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(512, seed: i));
            var trigger = i % 13 == 0 ? SnapshotTrigger.Manual : SnapshotTrigger.PostChange;
            var captured = await env.SnapshotService.CreateAsync(game.Id, trigger, $"n{i}", CancellationToken.None);
            allIds.Add(captured.SnapshotId!.Value);
            if (trigger == SnapshotTrigger.Manual)
            {
                manualIds.Add(captured.SnapshotId!.Value);
            }
        }

        env.Clock.Set(start.AddHours(39 * 12 + 1));

        var before = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);
        var plan = await env.Retention.PreviewAsync(game.Id, CancellationToken.None);
        var result = await env.Retention.ApplyAsync(plan, CancellationToken.None);

        var after = await env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);
        var afterIds = after.Select(s => s.Id).ToHashSet();

        _output.WriteLine($"[B7] 清理前 {before} 个 → 计划清理 {plan.Snapshots.Count} 个 / 释放 " +
                          $"{plan.BytesFreed} 字节 / 待删对象 {plan.ObjectsToDelete.Count} 个 → " +
                          $"清理后 {after.Count} 个（实际删对象 {result.DeletedObjects} 个）");

        Assert.Equal(40, before);
        Assert.True(plan.Snapshots.Count > 0, "40 个快照必须触发清理");
        Assert.True(after.Count < before);
        Assert.True(after.Count >= 20, "至少要保留 keepLast 个");
        Assert.Contains(allIds[^1], afterIds);
        Assert.All(manualIds, id => Assert.Contains(id, afterIds));
        Assert.All(plan.Snapshots, s => Assert.DoesNotContain(s.Id, manualIds));
    }

    [Fact]
    public async Task B8_ChangesWhileAppWasClosedAreCaughtUpAtStartup()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/B8", ("a.sav", TestEnvironment.Bytes(1024, seed: 1)));
        var game = await env.AddGameAsync("B8", default, dir);

        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var before = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);

        // 应用没运行期间改档（顺便把时钟拨后一点，保证时间线有序）
        env.Clock.Advance(TimeSpan.FromMinutes(5));
        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(4096, seed: 99));

        // 下次启动：全量比对
        var created = await env.Scheduler.RunStartupCatchUpAsync(CancellationToken.None);
        var snapshots = await env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);
        var catchUp = snapshots.Single(s => s.Id != first.SnapshotId);

        _output.WriteLine($"[B8] 启动补扫补出 {created} 个快照；总快照 {before} → {snapshots.Count}，" +
                          $"新快照 trigger={catchUp.Trigger}，备注={catchUp.Note}，大小={catchUp.TotalSize}");

        Assert.Equal(1, created);
        Assert.Equal(before + 1, snapshots.Count);
        Assert.Equal(SnapshotTrigger.PostChange, catchUp.Trigger);
        Assert.Contains("启动补扫", catchUp.Note);
        Assert.Equal(4096, catchUp.TotalSize);
        Assert.Equal(RemoteSnapshotState.Uploaded, catchUp.RemoteState);
    }

    [Fact]
    public async Task B9_PausedSchedulerSkipsAutomaticSnapshots()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("Saved Games/B9", ("a.sav", TestEnvironment.Bytes(256, seed: 1)));
        var game = await ConfigureGameAsync(env, "B9", dir);

        await env.Scheduler.StartAsync(CancellationToken.None);
        env.Scheduler.Pause(TimeSpan.FromMinutes(30));

        env.ProcessWatcher.RaiseExited(game);
        await Task.Delay(700);

        var snapshots = await env.Snapshots.CountAsync(game.Id, CancellationToken.None);
        _output.WriteLine($"[B9] 暂停期间 exit 触发后快照数={snapshots}（应为 0），IsPaused={env.Scheduler.IsPaused}");

        Assert.Equal(0, snapshots);
        Assert.True(env.Scheduler.IsPaused);
        Assert.NotNull(env.Scheduler.PausedUntil);
    }

    /// <summary>清单是"临时名 PUT + MOVE"，断言时去掉 .tmp-xxx 后缀。</summary>
    private static string StripTempSuffix(string request)
    {
        var marker = request.IndexOf(".tmp-", StringComparison.Ordinal);
        return marker < 0 ? request : request[..marker];
    }

    private static async Task<Game> ConfigureGameAsync(TestEnvironment env, string name, string saveDir)
    {
        var game = new Game
        {
            Name = name,
            InstallDir = @"C:\Games\" + name,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        game.ExeNames.Add(name.ToLowerInvariant());
        game.SaveRoots.Add(env.MakeSaveRoot(saveDir));

        await env.Games.UpsertAsync(game, CancellationToken.None);
        return game;
    }

    private static Task<int> CountAsync(TestEnvironment env, Guid gameId, SnapshotTrigger trigger) =>
        env.Snapshots.CountByTriggerAsync(gameId, trigger, CancellationToken.None);

    private static async Task<bool> WaitAsync(
        Func<Task<bool>> condition,
        string what,
        TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        _ = what;
        return false;
    }
}
