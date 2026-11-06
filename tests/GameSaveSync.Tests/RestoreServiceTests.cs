using GameSaveSync.Core.Models;
using GameSaveSync.Core.Restore;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

public sealed class RestoreServiceTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Restore_RoundTripsEveryFileByteForByte()
    {
        var dir = _env.CreateSaveDir("Saved Games/RoundTrip",
            ("slot1.sav", TestEnvironment.Bytes(1024, seed: 1)),
            ("sub/slot2.sav", TestEnvironment.Bytes(2048, seed: 2)),
            ("中文 目录/存档 1.sav", TestEnvironment.Bytes(512, seed: 3)));

        var game = await _env.AddGameAsync("RoundTrip", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var originalHashes = _env.HashTree(dir);

        // 破坏现场：改一个、删一个、加一个
        File.WriteAllBytes(Path.Combine(dir, "slot1.sav"), TestEnvironment.Bytes(1024, seed: 99));
        File.Delete(Path.Combine(dir, "sub", "slot2.sav"));
        File.WriteAllBytes(Path.Combine(dir, "extra.tmp"), TestEnvironment.Bytes(64));

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(plan.CanExecute);
        Assert.Equal(1, plan.AddCount);        // sub/slot2.sav
        Assert.Equal(1, plan.OverwriteCount);  // slot1.sav
        Assert.Equal(1, plan.DeleteCount);     // extra.tmp

        var result = await _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Failures.Select(f => f.RelPath + "=" + f.Reason)));
        Assert.Equal(2, result.AppliedCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(Path.Combine(dir, "extra.tmp")));

        var restored = _env.HashTree(dir);
        Assert.Equal(originalHashes.Count, restored.Count);
        foreach (var (relative, hash) in originalHashes)
        {
            Assert.True(restored.TryGetValue(relative, out var actual), $"缺少文件：{relative}");
            Assert.Equal(hash, actual);
        }
    }

    [Fact]
    public async Task Restore_CreatesPreRestoreSnapshotThatCanGoBack()
    {
        var dir = _env.CreateSaveDir("Saved Games/PreRestore", ("save.sav", TestEnvironment.Bytes(256, seed: 1)));
        var game = await _env.AddGameAsync("PreRestore", default, dir);

        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var modified = TestEnvironment.Bytes(256, seed: 42);
        File.WriteAllBytes(Path.Combine(dir, "save.sav"), modified);

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var result = await _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.NotNull(result.PreRestoreSnapshotId);
        var preRestore = await _env.Snapshots.GetAsync(result.PreRestoreSnapshotId!.Value, CancellationToken.None);
        Assert.Equal(SnapshotTrigger.PreRestore, preRestore!.Trigger);

        // 恢复到第一个快照后，文件内容回到旧版本
        Assert.Equal(TestEnvironment.HashFileWith(TestEnvironment.Bytes(256, seed: 1)),
            TestEnvironment.HashFile(Path.Combine(dir, "save.sav")));

        // 再从 pre_restore 回到"恢复前"的状态
        var backPlan = await _env.RestoreService.PlanAsync(preRestore.Id, CancellationToken.None);
        var backResult = await _env.RestoreService.ExecuteAsync(backPlan, null, CancellationToken.None);

        Assert.True(backResult.Success);
        Assert.Equal(modified, File.ReadAllBytes(Path.Combine(dir, "save.sav")));
    }

    [Fact]
    public async Task Restore_PreservesReadOnlyAttribute()
    {
        var dir = _env.CreateSaveDir("Saved Games/ReadOnly", ("save.sav", TestEnvironment.Bytes(128, seed: 1)));
        var game = await _env.AddGameAsync("ReadOnly", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var path = Path.Combine(dir, "save.sav");
        File.WriteAllBytes(path, TestEnvironment.Bytes(128, seed: 2));
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var result = await _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(TestEnvironment.Bytes(128, seed: 1), File.ReadAllBytes(path));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "只读属性丢失");

        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public async Task Restore_ReportsLockedFileButRestoresTheRest()
    {
        var dir = _env.CreateSaveDir("Saved Games/LockedRestore",
            ("a.sav", TestEnvironment.Bytes(128, seed: 1)),
            ("b.sav", TestEnvironment.Bytes(128, seed: 2)));
        var game = await _env.AddGameAsync("LockedRestore", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(128, seed: 9));
        File.WriteAllBytes(Path.Combine(dir, "b.sav"), TestEnvironment.Bytes(128, seed: 9));

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        using (new FileStream(Path.Combine(dir, "a.sav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Single(result.Failures);
            Assert.Equal("a.sav", result.Failures[0].RelPath);
            Assert.Equal(1, result.AppliedCount);
        }

        Assert.Equal(TestEnvironment.Bytes(128, seed: 2), File.ReadAllBytes(Path.Combine(dir, "b.sav")));
        // 失败的临时文件必须被清理掉
        Assert.Empty(Directory.GetFiles(dir, ".gssrestore-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Plan_RequiresRelocateWhenTokenMissing()
    {
        var dir = _env.CreateSaveDir("Saved Games/Relocate", ("save.sav", TestEnvironment.Bytes(16)));
        var game = await _env.AddGameAsync("Relocate", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        game.SaveRoots.Clear();
        game.SaveRoots.Add(new SaveRoot { Token = @"%NOPE%\saves", IsTokenized = true });
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.False(plan.CanExecute);
        Assert.NotEmpty(plan.UnresolvedRoots);
        await Assert.ThrowsAsync<RestoreRequiresRelocateException>(() =>
            _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None));
    }

    [Fact]
    public async Task Plan_ReportsUnchangedFilesWithoutTouchingThem()
    {
        var dir = _env.CreateSaveDir("Saved Games/Unchanged", ("save.sav", TestEnvironment.Bytes(64)));
        var game = await _env.AddGameAsync("Unchanged", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var before = File.GetLastWriteTimeUtc(Path.Combine(dir, "save.sav"));
        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.Empty(plan.Operations);
        Assert.Equal(1, plan.UnchangedCount);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(dir, "save.sav")));
    }

    [Fact]
    public async Task Plan_CanSkipDeletions()
    {
        var dir = _env.CreateSaveDir("Saved Games/NoDelete", ("save.sav", TestEnvironment.Bytes(64)));
        var game = await _env.AddGameAsync("NoDelete", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        File.WriteAllBytes(Path.Combine(dir, "keepme.dat"), TestEnvironment.Bytes(8));

        _env.RestoreService.DeleteExtraneousFiles = false;
        try
        {
            var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
            Assert.Equal(0, plan.DeleteCount);
        }
        finally
        {
            _env.RestoreService.DeleteExtraneousFiles = true;
        }
    }

    [Fact]
    public async Task Restore_RefusesCorruptedObjectAndKeepsFileIntact()
    {
        var dir = _env.CreateSaveDir("Saved Games/Corrupt", ("save.sav", TestEnvironment.Bytes(512, seed: 5)));
        var game = await _env.AddGameAsync("Corrupt", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        // 人为破坏远端/本地对象（模拟被截断）
        var files = await _env.Snapshots.GetFilesAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var hash = files[0].Hash;
        var objectPath = _env.Store.GetObjectPath(hash);
        var corrupted = File.ReadAllBytes(objectPath)[..100];
        File.WriteAllBytes(objectPath, corrupted);

        File.WriteAllBytes(Path.Combine(dir, "save.sav"), TestEnvironment.Bytes(512, seed: 77));

        var plan = await _env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var result = await _env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(result.Failures);
        Assert.Contains("校验失败", result.Failures[0].Reason, StringComparison.Ordinal);
        // 目标文件保持"恢复前"的内容，绝不能被半截数据覆盖
        Assert.Equal(TestEnvironment.Bytes(512, seed: 77), File.ReadAllBytes(Path.Combine(dir, "save.sav")));
    }
}
