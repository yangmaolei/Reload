using System.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;
using Xunit.Abstractions;

namespace GameSaveSync.Tests;

/// <summary>
/// M3 交付验收 7 条：两台机器（两个独立数据目录 + 同一个进程内 WebDAV）。
/// 说明：这里用"同一台机器上的两个独立环境"模拟两台电脑，账号/路径令牌各走各的，
/// 与真实跨机场景的差异只有底层磁盘与网络延迟。
/// </summary>
public sealed class M3AcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public M3AcceptanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task C1_BackupOnA_SeenByBWithin60s_RestoredByteIdentical()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C1",
            ("save.sav", TestEnvironment.Bytes(1024, seed: 1)),
            ("sub/deep.sav", TestEnvironment.Bytes(2048, seed: 2)));
        var game = await a.AddGameAsync("C1", default, dir);

        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var upload = await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(upload.Success, upload.Error);

        // B：全新机器，同一个 WebDAV；游戏还没装（先建空目录代表"已安装"）
        await using var b = TestEnvironment.CreateSecondDevice(a.Server!);
        var bDir = Path.Combine(b.ProfileDir, "Saved Games", "C1");
        Directory.CreateDirectory(bDir);

        Assert.Equal(TimeSpan.FromSeconds(60), RemoteIndexPoller.DefaultInterval);

        var poll = await b.Poller.PollOnceAsync(CancellationToken.None);
        var bGame = await b.Games.GetAsync(game.Id, CancellationToken.None);

        Assert.True(poll.Changed);
        Assert.Equal(1, poll.ImportedSnapshots);
        Assert.NotNull(bGame);
        Assert.Equal("C1", bGame!.Name);

        var plan = await b.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var restore = await b.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        var expected = a.HashTree(dir);
        var actual = b.HashTree(bDir);

        _output.WriteLine($"[C1] A 备份 {snapshot.FileCount} 文件 → B 轮询导入 {poll.ImportedSnapshots} 个快照 → " +
                          $"恢复写入 {restore.AppliedCount} 个、失败 {restore.Failures.Count}；逐文件哈希比对 {expected.Count} 项");

        Assert.True(restore.Success, string.Join(";", restore.Failures.Select(f => f.RelPath + "=" + f.Reason)));
        TestEnvironment.AssertTreesEqual(expected, actual);
    }

    [Fact]
    public async Task C2_BothDevicesEdit_ConflictMarkedAndNoSnapshotLost()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dirA = a.CreateSaveDir("Saved Games/C2", ("save.sav", TestEnvironment.Bytes(512, seed: 1)));
        var game = await a.AddGameAsync("C2", default, dirA);

        var baseline = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(baseline.SnapshotId!.Value, CancellationToken.None)).Success);

        // B 上线，导入基线快照（成为 B 的时间线父节点）
        await using var b = TestEnvironment.CreateSecondDevice(a.Server!);
        var bDir = Path.Combine(b.ProfileDir, "Saved Games", "C2");
        Directory.CreateDirectory(bDir);
        await b.Poller.PollOnceAsync(CancellationToken.None);

        // B 改档并上传（B 的 seq=1）
        File.WriteAllBytes(Path.Combine(bDir, "save.sav"), TestEnvironment.Bytes(512, seed: 22));
        var bSnapshot = await b.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "B-edit", CancellationToken.None);
        var bUpload = await b.Uploads.UploadSnapshotAsync(bSnapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(bUpload.Success, bUpload.Error);

        // A 拔网线，改档并尝试上传（进 outbox）
        a.GoOffline();
        File.WriteAllBytes(Path.Combine(dirA, "save.sav"), TestEnvironment.Bytes(512, seed: 33));
        var aSnapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "A-edit", CancellationToken.None);
        var offlineUpload = await a.Uploads.UploadSnapshotAsync(aSnapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.False(offlineUpload.Success);
        Assert.Single(await a.Snapshots.ListPendingAsync(CancellationToken.None));

        // A 恢复网络，重试上传 → 合并应识别为分叉
        a.GoOnline();
        var retried = await a.Uploads.UploadPendingAsync(CancellationToken.None);
        Assert.All(retried, r => Assert.True(r.Success, r.Error));

        var index = IndexSerializer.TryParse(a.Server!.GetText("GameSaveSync/index.json"))!;
        var entry = index.Games[game.Id.ToString("D")];

        // 两个快照的清单都必须还在远端
        var manifests = a.Server.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/")
            .Where(k => !k.Contains(".tmp-", StringComparison.Ordinal))
            .ToList();

        var pendingAfter = await a.Outbox.CountPendingAsync(CancellationToken.None);

        _output.WriteLine($"[C2] index.conflict={entry.Conflict}，推荐 snapshot={entry.SnapshotId:N}（A={aSnapshot.SnapshotId:N} B={bSnapshot.SnapshotId:N}），" +
                          $"远端清单 {manifests.Count} 个，A 的 outbox 待办={pendingAfter}，" +
                          $"重试结果 {retried.Count(r => r.Success)}/{retried.Count}；" +
                          $"A device={(await a.Devices.GetLocalIdAsync(CancellationToken.None))[..8]}… " +
                          $"B device={(await b.Devices.GetLocalIdAsync(CancellationToken.None))[..8]}…");

        Assert.True(entry.Conflict, "两台设备各改一次必须标记冲突");
        Assert.Equal(3, manifests.Count);   // 基线 + A 的改动 + B 的改动，一个都不能少
        Assert.Contains(manifests, k => k.Contains(bSnapshot.SnapshotId!.Value.ToString("N"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manifests, k => k.Contains(aSnapshot.SnapshotId!.Value.ToString("N"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, pendingAfter);   // 没有无限重试
    }

    [Fact]
    public async Task C3_PreconditionFailedOnIndex_MergesAndLosesNothing()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C3", ("a.sav", TestEnvironment.Bytes(300, seed: 4)));
        var game = await a.AddGameAsync("C3", default, dir);

        var first = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(first.SnapshotId!.Value, CancellationToken.None)).Success);

        // 模拟"别的设备刚改过索引"（GET 之后、PUT 之前被篡改）+ 注入一次 412
        var otherGameId = Guid.NewGuid();
        var external = new IndexDocument
        {
            UpdatedAt = 1,
            Games =
            {
                [otherGameId.ToString("D")] = new IndexGameEntry
                {
                    Seq = 7,
                    SnapshotId = Guid.NewGuid(),
                    DeviceId = "device-other",
                    UpdatedAt = 1,
                },
            },
        };
        a.Server!.Overwrite("GameSaveSync/index.json",
            System.Text.Encoding.UTF8.GetBytes(IndexSerializer.ToJson(external)));
        a.Server.FailIndexPutOnce = 1;

        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(300, seed: 5));
        var second = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var upload = await a.Uploads.UploadSnapshotAsync(second.SnapshotId!.Value, CancellationToken.None);

        var index = IndexSerializer.TryParse(a.Server.GetText("GameSaveSync/index.json"))!;
        var snapshots = await a.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);

        _output.WriteLine($"[C3] 412 后重试：success={upload.Success}，索引里 {index.Games.Count} 个游戏，" +
                          $"本地快照 {snapshots.Count} 个，A 的条目 seq={index.Games[game.Id.ToString("D")].Seq}");

        Assert.True(upload.Success, upload.Error);
        Assert.True(index.Games.ContainsKey(otherGameId.ToString("D")), "不能丢掉别的设备的条目");
        Assert.True(index.Games.ContainsKey(game.Id.ToString("D")));
        Assert.Equal(second.SnapshotId, index.Games[game.Id.ToString("D")].SnapshotId);
        Assert.Equal(2, snapshots.Count);
    }

    [Fact]
    public async Task C4_MissingGameFolder_NeedsRelocateUntilUserMapsIt()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C4", ("save.sav", TestEnvironment.Bytes(768, seed: 6)));
        var game = await a.AddGameAsync("C4", default, dir);
        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None)).Success);

        // B 上**没有**这个游戏的目录
        await using var b = TestEnvironment.CreateSecondDevice(a.Server!);
        await b.Poller.PollOnceAsync(CancellationToken.None);

        var bGame = (await b.Games.GetAsync(game.Id, CancellationToken.None))!;
        var planBefore = await b.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.True(bGame.NeedsRelocate);
        Assert.False(planBefore.CanExecute, "找不到目录时必须拒绝恢复，绝不猜路径");

        // 用户手动把该存档根映射到本机某个目录
        var mapped = Path.Combine(b.ProfileDir, "MappedSaves", "C4");
        Directory.CreateDirectory(mapped);
        var token = bGame.SaveRoots[0].Token;
        bGame.PathOverrides[token] = mapped;
        bGame.NeedsRelocate = false;
        await b.Games.UpsertAsync(bGame, CancellationToken.None);

        var planAfter = await b.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var restore = await b.RestoreService.ExecuteAsync(planAfter, null, CancellationToken.None);

        _output.WriteLine($"[C4] 重定位前 CanExecute={planBefore.CanExecute}（未解析 {planBefore.UnresolvedRoots.Count} 项）→ " +
                          $"映射到 {mapped} 后写入 {restore.AppliedCount} 个文件");

        Assert.True(planAfter.CanExecute);
        Assert.True(restore.Success);
        TestEnvironment.AssertTreesEqual(a.HashTree(dir), b.HashTree(mapped));
    }

    [Fact]
    public async Task C5_TruncatedRemoteObject_FailsLoudlyAndCanBeRetransferred()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C5", ("save.sav", TestEnvironment.Bytes(4096, seed: 7)));
        var game = await a.AddGameAsync("C5", default, dir);
        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None)).Success);

        var files = await a.Snapshots.GetFilesAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var hash = files[0].Hash;
        var objectKey = $"GameSaveSync/objects/{hash[..2]}/{hash}";
        var original = a.Server!.Get(objectKey)!;

        // 人为截断远端对象
        a.Server.Overwrite(objectKey, original[..100]);

        await using var b = TestEnvironment.CreateSecondDevice(a.Server!);
        var poll = await b.Poller.PollOnceAsync(CancellationToken.None);

        var bSnapshotMissing = await b.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var casFiles = Directory.EnumerateFiles(Path.Combine(b.DataRoot, "objects"), "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"[C5] 截断对象后 B 导入：imported={poll.ImportedSnapshots}，快照入库={bSnapshotMissing is not null}，" +
                          $"CAS 残留 {casFiles.Count} 个（应为 0），日志含校验失败=" +
                          $"{b.Logger.Recent(200).Any(l => l.Message.Contains("校验") || l.Message.Contains("导入远端快照失败"))}");

        Assert.Equal(0, poll.ImportedSnapshots);
        Assert.Null(bSnapshotMissing);
        Assert.Empty(casFiles);   // 损坏对象不能被半截写进 CAS

        // 恢复正确对象后重试 → 成功
        a.Server.Overwrite(objectKey, original);
        var retry = await b.Poller.PollOnceAsync(CancellationToken.None);
        var restoredSnapshot = await b.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.Equal(1, retry.ImportedSnapshots);
        Assert.NotNull(restoredSnapshot);
    }

    [Fact]
    public async Task C6_CorruptIndex_RebuildsInsteadOfCrashing()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C6", ("save.sav", TestEnvironment.Bytes(256, seed: 8)));
        var game = await a.AddGameAsync("C6", default, dir);
        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None)).Success);

        // 人为写坏 index.json
        a.Server!.Overwrite("GameSaveSync/index.json",
            System.Text.Encoding.UTF8.GetBytes("{ this is not valid json ###"));

        await using var b = TestEnvironment.CreateSecondDevice(a.Server);
        var poll = await b.Poller.PollOnceAsync(CancellationToken.None);

        var repaired = IndexSerializer.TryParse(a.Server.GetText("GameSaveSync/index.json"));

        _output.WriteLine($"[C6] 索引损坏后：rebuilt={poll.Rebuilt}，imported={poll.ImportedSnapshots}，" +
                          $"重建后的索引 {(repaired is null ? "仍非法" : $"含 {repaired.Games.Count} 个游戏")}");

        Assert.True(poll.Rebuilt);
        Assert.NotNull(repaired);
        Assert.True(repaired!.Games.ContainsKey(game.Id.ToString("D")));
        Assert.NotNull(await b.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None));
    }

    [Fact]
    public async Task C7_PollingVolumeMatchesSixtySecondCadence()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C7", ("save.sav", TestEnvironment.Bytes(128, seed: 9)));
        var game = await a.AddGameAsync("C7", default, dir);
        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None)).Success);

        await using var b = TestEnvironment.CreateSecondDevice(a.Server!);
        var logStart = a.Server!.RequestLog.Count;

        const int polls = 10;
        var changed = 0;
        for (var i = 0; i < polls; i++)
        {
            var result = await b.Poller.PollOnceAsync(CancellationToken.None);
            if (result.Changed)
            {
                changed++;
            }
        }

        var indexGets = a.Server.RequestLog.Skip(logStart).Count(r => r == "GET GameSaveSync/index.json");
        var notModified = polls - changed;
        var perDay = TimeSpan.FromHours(24) / RemoteIndexPoller.DefaultInterval;

        _output.WriteLine($"[C7] {polls} 次轮询 → index GET {indexGets} 次（其中 304 {notModified} 次），" +
                          $"默认轮询间隔 {RemoteIndexPoller.DefaultInterval.TotalSeconds:0}s，24 小时预计 {perDay:0} 次请求");

        Assert.Equal(polls, indexGets);          // 每次轮询只发一个请求
        Assert.Equal(1, changed);                // 只有第一次有变化
        Assert.Equal(polls - 1, notModified);    // 其余全是 304
        Assert.Equal(1440, (int)perDay);
    }

    [Fact]
    public async Task C8_DeviceRegistration_IsVisibleToBothSides()
    {
        await using var a = TestEnvironment.Create(withWebDav: true);
        var dir = a.CreateSaveDir("Saved Games/C8", ("save.sav", TestEnvironment.Bytes(64, seed: 10)));
        var game = await a.AddGameAsync("C8", default, dir);
        var snapshot = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.True((await a.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None)).Success);

        var localId = await a.Devices.GetLocalIdAsync(CancellationToken.None);
        var remoteDevices = await a.IndexService.RefreshDevicesAsync(CancellationToken.None);
        var localRow = await a.Devices.GetLocalAsync(CancellationToken.None);

        var deviceFiles = a.Server!.KeysWithPrefix("GameSaveSync/devices/");

        _output.WriteLine($"[C8] 本地设备 {localRow.Name}（seq={localRow.Seq}），远端 devices/*.json {deviceFiles.Count} 个，" +
                          $"刷新到 {remoteDevices.Count} 台设备");

        Assert.Contains(deviceFiles, k => k.Contains(localId, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(remoteDevices, d => d.Id == localId);
        Assert.NotNull(IndexSerializer.TryParseDevice(a.Server.GetText($"GameSaveSync/devices/{localId}.json")));
        Assert.True(localRow.Seq >= 1);
    }

    private static long Elapsed(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }
}
