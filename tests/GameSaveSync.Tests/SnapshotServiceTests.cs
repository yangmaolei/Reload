using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

public sealed class SnapshotServiceTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task CreateAsync_CapturesRecursiveTreeWithCorrectCounts()
    {
        var dir = _env.CreateSaveDir("Saved Games/Counts",
            ("slot1.sav", TestEnvironment.Bytes(100)),
            ("slot2.sav", TestEnvironment.Bytes(200)),
            ("sub/a.dat", TestEnvironment.Bytes(300)),
            ("sub/deep/b.dat", TestEnvironment.Bytes(400)));

        var game = await _env.AddGameAsync("Counts", default, dir);
        var result = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "首次", CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(1000, result.TotalSize);
        Assert.Empty(result.Skipped);

        var snapshot = await _env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot!.FileCount);
        Assert.Equal(RemoteSnapshotState.Pending, snapshot.RemoteState);
        Assert.Null(snapshot.ParentId);
        Assert.Equal("首次", snapshot.Note);
    }

    [Fact]
    public async Task CreateAsync_HonorsExcludePatterns()
    {
        var dir = _env.CreateSaveDir("Saved Games/Exclude",
            ("save1.sav", TestEnvironment.Bytes(10)),
            ("debug.log", TestEnvironment.Bytes(10)),
            ("cache/blob.bin", TestEnvironment.Bytes(10)));

        var game = new Game { Name = "Exclude" };
        game.SaveRoots.Add(_env.MakeSaveRootWithIncludes(dir, Array.Empty<string>(), new[] { "*.log", "cache/*" }));
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        var result = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        var files = await _env.Snapshots.GetFilesAsync(result.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal("save1.sav", Assert.Single(files).RelPath);
    }

    [Fact]
    public async Task CreateAsync_SkipsLockedFileButKeepsSnapshot()
    {
        var dir = _env.CreateSaveDir("Saved Games/Locked",
            ("a.sav", TestEnvironment.Bytes(128)),
            ("b.sav", TestEnvironment.Bytes(128, seed: 3)));

        var game = await _env.AddGameAsync("Locked", default, dir);

        using (new FileStream(Path.Combine(dir, "b.sav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _env.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

            Assert.True(result.Created);
            Assert.Equal(1, result.FileCount);
            Assert.Single(result.Skipped);
            Assert.Equal("b.sav", result.Skipped[0].RelPath);

            var snapshot = await _env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None);
            Assert.Equal(1, snapshot!.SkippedCount);
        }
    }

    [Fact]
    public async Task CreateAsync_RefusesEmptySnapshotWhenDirectoryHasNoFiles()
    {
        var dir = Path.Combine(_env.ProfileDir, "Saved Games", "Empty");
        Directory.CreateDirectory(dir);

        var game = await _env.AddGameAsync("Empty", default, dir);

        await Assert.ThrowsAsync<NoFilesFoundException>(() =>
            _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None));

        Assert.Equal(0, await _env.Snapshots.CountAsync(game.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_ThrowsNeedsRelocateWhenDirectoryDeleted()
    {
        var dir = _env.CreateSaveDir("Saved Games/WillDie", ("a.sav", TestEnvironment.Bytes(10)));
        var game = await _env.AddGameAsync("WillDie", default, dir);

        Directory.Delete(dir, recursive: true);

        await Assert.ThrowsAsync<NeedsRelocateException>(() =>
            _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None));

        var reloaded = await _env.Games.GetAsync(game.Id, CancellationToken.None);
        Assert.True(reloaded!.NeedsRelocate);
        Assert.Equal(0, await _env.Snapshots.CountAsync(game.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_SetsParentIdChain()
    {
        var dir = _env.CreateSaveDir("Saved Games/Chain", ("a.sav", TestEnvironment.Bytes(32)));
        var game = await _env.AddGameAsync("Chain", default, dir);

        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(64, seed: 9));
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var secondSnapshot = await _env.Snapshots.GetAsync(second.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(first.SnapshotId, secondSnapshot!.ParentId);
    }

    [Fact]
    public async Task CreateAsync_ReusesObjectsForUnchangedFiles()
    {
        var dir = _env.CreateSaveDir("Saved Games/Reuse",
            ("a.sav", TestEnvironment.Bytes(256)),
            ("b.sav", TestEnvironment.Bytes(256, seed: 7)));

        var game = await _env.AddGameAsync("Reuse", default, dir);

        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        Assert.Equal(2, first.NewObjects);

        File.WriteAllBytes(Path.Combine(dir, "b.sav"), TestEnvironment.Bytes(256, seed: 11));
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, second.NewObjects);   // 只有 b.sav 变了
        Assert.Equal(1, second.ReusedObjects);
    }

    [Fact]
    public async Task CreateAsync_StoresManifestOnDiskAndWritesDeviceId()
    {
        var dir = _env.CreateSaveDir("Saved Games/Manifest", ("a.sav", TestEnvironment.Bytes(16)));
        var game = await _env.AddGameAsync("Manifest", default, dir);

        var result = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "note", CancellationToken.None);
        var snapshot = (await _env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None))!;

        var deviceId = await _env.Settings.GetDeviceIdAsync(CancellationToken.None);
        Assert.Equal(deviceId, snapshot.DeviceId);

        var manifestPath = Path.Combine(
            _env.DataRoot, "manifests", game.Id.ToString("D"), SnapshotNames.ManifestFileName(snapshot));
        Assert.True(File.Exists(manifestPath));

        var manifest = SnapshotManifestSerializer.FromJson(File.ReadAllText(manifestPath));
        Assert.Equal(snapshot.Id, manifest.SnapshotId);
        Assert.Equal("manual", manifest.Trigger);
        Assert.Contains("%SAVEDGAMES%", manifest.Roots[0].Token);
        Assert.Equal(snapshot.ManifestHash, SnapshotManifestSerializer.ComputeHash(manifest, GameSaveSync.Core.Hashing.Blake3HashService.Instance));
    }

    [Fact]
    public async Task CreateAsync_KeepsBothSnapshotsAndBothObjectsAfterModification()
    {
        var dir = _env.CreateSaveDir("Saved Games/TwoVersions", ("save.sav", TestEnvironment.Bytes(1024, seed: 1)));
        var game = await _env.AddGameAsync("TwoVersions", default, dir);

        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var oldHash = TestEnvironment.HashFileWith(TestEnvironment.Bytes(1024, seed: 1));

        File.WriteAllBytes(Path.Combine(dir, "save.sav"), TestEnvironment.Bytes(1024, seed: 2));
        var newHash = TestEnvironment.HashFileWith(TestEnvironment.Bytes(1024, seed: 2));
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var all = await _env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.NotEqual(oldHash, newHash);
        Assert.True(_env.Store.Contains(oldHash));
        Assert.True(_env.Store.Contains(newHash));
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
    }

    [Fact]
    public async Task CreateAsync_BacksUpAvailableRootAndFlagsWhenAnotherRootMissing()
    {
        var alive = _env.CreateSaveDir("Saved Games/PartialAlive", ("a.sav", TestEnvironment.Bytes(128)));
        var gone = _env.CreateSaveDir("Saved Games/PartialGone", ("b.sav", TestEnvironment.Bytes(128, seed: 2)));

        var game = new Game { Name = "Partial" };
        game.SaveRoots.Add(_env.MakeSaveRoot(alive));
        game.SaveRoots.Add(_env.MakeSaveRoot(gone));
        await _env.Games.UpsertAsync(game, CancellationToken.None);

        Directory.Delete(gone, recursive: true);

        var result = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        var reloaded = await _env.Games.GetAsync(game.Id, CancellationToken.None);
        Assert.True(reloaded!.NeedsRelocate, "部分存档根丢失时必须标记需重新定位");
    }

    [Fact]
    public async Task CreateAsync_ClearsNeedsRelocateFlagAfterSuccessfulBackup()
    {
        var dir = _env.CreateSaveDir("Saved Games/FlagReset", ("a.sav", TestEnvironment.Bytes(32)));
        var game = await _env.AddGameAsync("FlagReset", default, dir);

        await _env.Games.SetNeedsRelocateAsync(game.Id, true, CancellationToken.None);
        var result = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        Assert.True(result.Created);
        var reloaded = await _env.Games.GetAsync(game.Id, CancellationToken.None);
        Assert.False(reloaded!.NeedsRelocate);
    }

    [Fact]
    public async Task CreateAsync_RecordsSkippedFilesInManifest()
    {
        var dir = _env.CreateSaveDir("Saved Games/SkippedManifest",
            ("a.sav", TestEnvironment.Bytes(64)),
            ("b.sav", TestEnvironment.Bytes(64, seed: 2)));
        var game = await _env.AddGameAsync("SkippedManifest", default, dir);

        using (new FileStream(Path.Combine(dir, "b.sav"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _env.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

            var snapshot = (await _env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None))!;
            var manifest = await _env.ManifestStore.LoadAsync(snapshot, CancellationToken.None);
            Assert.Single(manifest.Skipped);
            Assert.Equal("b.sav", manifest.Skipped[0].RelPath);
        }
    }
}
