using System.IO.Compression;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Tests.TestSupport;
using Xunit.Abstractions;

namespace GameSaveSync.Tests;

/// <summary>冲突视图 / 冲突解决 / 导出 zip。</summary>
public sealed class M3ConflictTests
{
    private readonly ITestOutputHelper _output;

    public M3ConflictTests(ITestOutputHelper output) => _output = output;

    private static async Task<(TestEnvironment A, TestEnvironment B, Guid GameId, Guid Baseline, Guid AEdit, Guid BEdit)>
        BuildConflictAsync()
    {
        var a = TestEnvironment.Create(withWebDav: true);
        var dirA = a.CreateSaveDir("Saved Games/Conflict", ("save.sav", TestEnvironment.Bytes(512, seed: 1)));
        var game = await a.AddGameAsync("Conflict", default, dirA);

        var baseline = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "base", CancellationToken.None);
        await a.Uploads.UploadSnapshotAsync(baseline.SnapshotId!.Value, CancellationToken.None);

        var b = TestEnvironment.CreateSecondDevice(a.Server!);
        var dirB = Path.Combine(b.ProfileDir, "Saved Games", "Conflict");
        Directory.CreateDirectory(dirB);
        await b.Poller.PollOnceAsync(CancellationToken.None);

        File.WriteAllBytes(Path.Combine(dirB, "save.sav"), TestEnvironment.Bytes(512, seed: 22));
        var bEdit = await b.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "B", CancellationToken.None);
        await b.Uploads.UploadSnapshotAsync(bEdit.SnapshotId!.Value, CancellationToken.None);

        // A 拔网线期间改档：新快照的 parent 仍是 baseline（没看到 B 的改动）→ 与 B 分叉
        a.GoOffline();
        File.WriteAllBytes(Path.Combine(dirA, "save.sav"), TestEnvironment.Bytes(512, seed: 33));
        var aEdit = await a.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "A", CancellationToken.None);
        await a.Uploads.UploadSnapshotAsync(aEdit.SnapshotId!.Value, CancellationToken.None);

        // 恢复网络：先把 B 的快照拉下来（冲突视图要能并排显示），再重试上传触发合并
        a.GoOnline();
        await a.Poller.PollOnceAsync(CancellationToken.None);
        await a.Uploads.UploadPendingAsync(CancellationToken.None);

        return (a, b, game.Id, baseline.SnapshotId!.Value, aEdit.SnapshotId!.Value, bEdit.SnapshotId!.Value);
    }

    [Fact]
    public async Task ConflictView_ShowsBothSidesWithDiffAndResolves()
    {
        var (a, b, gameId, _, aEdit, bEdit) = await BuildConflictAsync();
        await using var _a = a;
        await using var _b = b;

        var entry = await a.IndexState.GetRemoteAsync(gameId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.True(entry!.Conflict);

        var view = await a.Conflicts.GetAsync(gameId, CancellationToken.None);
        Assert.NotNull(view);
        Assert.NotEqual(view!.Left.Snapshot.DeviceId, view.Right.Snapshot.DeviceId);
        Assert.NotNull(view.AncestorId);

        var ids = new[] { view.Left.Snapshot.Id, view.Right.Snapshot.Id };
        Assert.Contains(aEdit, ids);
        Assert.Contains(bEdit, ids);

        _output.WriteLine($"[冲突视图] 左：{view.Left.Describe()}");
        _output.WriteLine($"[冲突视图] 右：{view.Right.Describe()}");

        var resolved = await a.Conflicts.ResolveAsync(gameId, aEdit, CancellationToken.None);
        Assert.True(resolved);

        var after = await a.IndexState.GetRemoteAsync(gameId, CancellationToken.None);
        Assert.NotNull(after);
        Assert.False(after!.Conflict);
        Assert.Equal(aEdit, after.SnapshotId);
        Assert.Null(await a.Conflicts.GetAsync(gameId, CancellationToken.None));
    }

    [Fact]
    public async Task Exporter_WritesZipWithAllFiles()
    {
        var (a, b, _, baseline, _, _) = await BuildConflictAsync();
        await using var _a = a;
        await using var _b = b;

        var zipPath = Path.Combine(a.Root, "export", "baseline.zip");
        var exporter = new SnapshotExporter(a.Snapshots, a.Games, a.Store, a.FileSystem, a.Logger);
        await exporter.ExportAsync(baseline, zipPath, CancellationToken.None);

        Assert.True(File.Exists(zipPath));

        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        _output.WriteLine($"[导出] {zipPath} 内含 {names.Count} 项：{string.Join(", ", names)}");

        Assert.Single(names);
        Assert.Equal("save.sav", names[0]);
    }
}
