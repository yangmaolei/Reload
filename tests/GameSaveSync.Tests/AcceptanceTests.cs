using System.Diagnostics;
using System.Text.Json;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;
using Xunit.Abstractions;

namespace GameSaveSync.Tests;

/// <summary>
/// M1 交付验收的 8 条（逐条对应 Prompt 里的验收清单），输出实测数据。
/// </summary>
[Collection("Acceptance")]
public sealed class AcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public AcceptanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A1_LargeSaveSet_SnapshotCountsMatchReality()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = Path.Combine(env.ProfileDir, "Saved Games", "BigGame");
        Directory.CreateDirectory(dir);

        const int fileCount = 5000;
        long totalBytes = 0;
        var payload = TestEnvironment.Bytes(64 * 1024);

        for (var i = 0; i < fileCount; i++)
        {
            var sub = Path.Combine(dir, $"slot{i % 10:D2}");
            Directory.CreateDirectory(sub);
            var content = new byte[1024 + (i % 7) * 16];
            Buffer.BlockCopy(payload, 0, content, 0, content.Length);
            BitConverter.TryWriteBytes(content.AsSpan(0, 8), (long)i);   // 保证每个文件内容唯一
            content[^1] = (byte)i;
            File.WriteAllBytes(Path.Combine(sub, $"save_{i:D5}.sav"), content);
            totalBytes += content.Length;
        }

        var game = await env.AddGameAsync("BigGame", default, dir);

        var stopwatch = Stopwatch.StartNew();
        var result = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "5000 files", CancellationToken.None);
        stopwatch.Stop();

        var snapshot = (await env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None))!;
        _output.WriteLine($"[A1] 文件数={snapshot.FileCount}（期望 {fileCount}），总字节={snapshot.TotalSize}（期望 {totalBytes}），" +
                          $"耗时={stopwatch.ElapsedMilliseconds} ms，所有对象复用={result.ReusedObjects}");

        Assert.Equal(fileCount, snapshot.FileCount);
        Assert.Equal(totalBytes, snapshot.TotalSize);
        Assert.Empty(result.Skipped);
        Assert.Equal(fileCount, result.NewObjects);
        Assert.Equal(0, result.ReusedObjects);

        // 与磁盘实际统计对账
        var onDisk = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        var onDiskBytes = onDisk.Sum(f => new FileInfo(f).Length);
        Assert.Equal(onDisk.Count, snapshot.FileCount);
        Assert.Equal(onDiskBytes, snapshot.TotalSize);

        // 每个对象的哈希必须与内容一致（CAS 自洽）
        var files = await env.Snapshots.GetFilesAsync(snapshot.Id, CancellationToken.None);
        foreach (var file in files.Take(50))
        {
            Assert.Equal(file.Hash, TestEnvironment.HashFileWith(File.ReadAllBytes(Path.Combine(dir, file.RelPath))));
        }
    }

    [Fact]
    public async Task A2_ModifyOneSaveFile_BothSnapshotsKeptAndBothObjectsPresent()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/A2",
            ("slot1.sav", TestEnvironment.Bytes(4096, seed: 1)),
            ("slot2.sav", TestEnvironment.Bytes(4096, seed: 2)));
        var game = await env.AddGameAsync("A2", default, dir);

        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "v1", CancellationToken.None);
        var oldHash = TestEnvironment.HashFile(Path.Combine(dir, "slot1.sav"));

        var newContent = TestEnvironment.Bytes(4096, seed: 77);
        File.WriteAllBytes(Path.Combine(dir, "slot1.sav"), newContent);
        var newHash = TestEnvironment.HashFileWith(newContent);

        var second = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "v2", CancellationToken.None);

        var all = await env.Snapshots.ListByGameAsync(game.Id, CancellationToken.None);
        _output.WriteLine($"[A2] 快照数={all.Count}，旧哈希={oldHash[..12]}…，新哈希={newHash[..12]}…，" +
                          $"新对象={second.NewObjects}，复用={second.ReusedObjects}");

        Assert.Equal(2, all.Count);
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.True(env.Store.Contains(oldHash), "旧内容对象必须仍然存在");
        Assert.True(env.Store.Contains(newHash), "新内容对象必须存在");
        Assert.Equal(1, second.NewObjects);
        Assert.Equal(1, second.ReusedObjects);
    }

    [Fact]
    public async Task A3_RestoreFirstSnapshot_AllFilesByteIdentical()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/A3",
            ("slot1.sav", TestEnvironment.Bytes(3000, seed: 1)),
            ("nested/deep/slot2.sav", TestEnvironment.Bytes(1500, seed: 2)),
            ("中文 存档.sav", TestEnvironment.Bytes(800, seed: 3)));

        var game = await env.AddGameAsync("A3", default, dir);
        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var expected = env.HashTree(dir);

        File.WriteAllBytes(Path.Combine(dir, "slot1.sav"), TestEnvironment.Bytes(3000, seed: 99));
        File.WriteAllText(Path.Combine(dir, "nested", "deep", "slot2.sav"), "corrupted");
        File.Delete(Path.Combine(dir, "中文 存档.sav"));
        File.WriteAllText(Path.Combine(dir, "junk.tmp"), "junk");

        var plan = await env.RestoreService.PlanAsync(first.SnapshotId!.Value, CancellationToken.None);
        var result = await env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);
        var actual = env.HashTree(dir);

        _output.WriteLine($"[A3] 计划：{plan.Describe()}；执行：写入 {result.AppliedCount}，删除 {result.DeletedCount}，" +
                          $"失败 {result.Failures.Count}，耗时 {result.ElapsedMs} ms");

        Assert.True(result.Success);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (path, hash) in expected)
        {
            Assert.True(actual.TryGetValue(path, out var restoredHash), $"缺少 {path}");
            Assert.Equal(hash, restoredHash);
        }
    }

    [Fact]
    public async Task A4_PreRestoreSnapshotExistsAndCanRevert()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/A4", ("save.sav", TestEnvironment.Bytes(1024, seed: 1)));
        var game = await env.AddGameAsync("A4", default, dir);

        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var beforeRestoreBytes = TestEnvironment.Bytes(1024, seed: 55);
        File.WriteAllBytes(Path.Combine(dir, "save.sav"), beforeRestoreBytes);

        var plan = await env.RestoreService.PlanAsync(first.SnapshotId!.Value, CancellationToken.None);
        var result = await env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        var preRestore = await env.Snapshots.GetAsync(result.PreRestoreSnapshotId!.Value, CancellationToken.None);

        _output.WriteLine($"[A4] pre_restore 快照={preRestore!.Id:N}，trigger={preRestore.Trigger}，文件数={preRestore.FileCount}");

        Assert.Equal(SnapshotTrigger.PreRestore, preRestore.Trigger);
        Assert.Equal(1, preRestore.FileCount);

        var revertPlan = await env.RestoreService.PlanAsync(preRestore.Id, CancellationToken.None);
        await env.RestoreService.ExecuteAsync(revertPlan, null, CancellationToken.None);

        Assert.Equal(beforeRestoreBytes, File.ReadAllBytes(Path.Combine(dir, "save.sav")));
    }

    [Fact]
    public async Task A5_OfflineBackupStaysLocalThenRetryUploads()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/A5", ("save.sav", TestEnvironment.Bytes(2048, seed: 12)));
        var game = await env.AddGameAsync("A5", default, dir);

        env.Server!.FailPutStatus = 503;                    // 模拟断网
        var snapshot = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var failedUpload = await env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        var pending = (await env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None))!;

        _output.WriteLine($"[A5] 断网时：本地快照已落盘（{pending.FileCount} 文件 / {pending.TotalSize} 字节），" +
                          $"remote_state={pending.RemoteState}，错误={pending.LastError}");

        Assert.False(failedUpload.Success);
        Assert.Equal(RemoteSnapshotState.Pending, pending.RemoteState);

        env.Server.FailPutStatus = 0;                       // 恢复网络
        var retried = await env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var uploaded = (await env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None))!;

        _output.WriteLine($"[A5] 重试后：remote_state={uploaded.RemoteState}，新对象={retried.ObjectsUploaded}");

        Assert.True(retried.Success);
        Assert.Equal(RemoteSnapshotState.Uploaded, uploaded.RemoteState);
        Assert.NotEmpty(env.Server.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/"));
    }

    [Fact]
    public async Task A6_KillDuringBackup_LeavesNoPartialObjects()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = Path.Combine(env.ProfileDir, "Saved Games", "A6");
        Directory.CreateDirectory(dir);

        for (var i = 0; i < 400; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"f{i:D4}.sav"), TestEnvironment.Bytes(64 * 1024, seed: i));
        }

        var game = await env.AddGameAsync("A6", default, dir);

        // 用取消令牌模拟"备份进行中被强杀"：快照根本不会写库
        using var cts = new CancellationTokenSource();
        var snapshotTask = env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, cts.Token);

        await Task.Delay(120);
        cts.Cancel();

        try
        {
            await snapshotTask;
        }
        catch (OperationCanceledException)
        {
            // 预期
        }

        Assert.Equal(0, await env.Snapshots.CountAsync(game.Id, CancellationToken.None));

        var objectFiles = Directory.EnumerateFiles(Path.Combine(env.DataRoot, "objects"), "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // 关键不变量：CAS 里任何对象必须是"内容与文件名一致"的完整对象
        foreach (var path in objectFiles)
        {
            var name = Path.GetFileName(path);
            var actualHash = TestEnvironment.HashFile(path);
            Assert.Equal(name, actualHash);
        }

        // 崩溃残留的临时文件必须能被启动清理干跑掉
        var leftover = Path.Combine(env.DataRoot, "objects", "tmp", $"{Guid.NewGuid():N}.part");
        File.WriteAllBytes(leftover, new byte[1000]);
        var cleaned = env.Store.CleanupTemporaryFiles();

        _output.WriteLine($"[A6] 取消后 CAS 完整对象 {objectFiles.Count} 个（全部哈希自洽），" +
                          $"无半截对象；启动清理删除 {cleaned} 个临时文件，快照数=0");

        Assert.False(File.Exists(leftover));
        Assert.Empty(Directory.GetFiles(Path.Combine(env.DataRoot, "objects", "tmp")));
    }

    [Fact]
    public async Task A7_UnicodeSpacesAndVeryLongPathsBackupAndRestore()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var unicodeDir = env.CreateSaveDir("Saved Games/中文 游戏 🎮",
            ("存档 一号.sav", TestEnvironment.Bytes(512, seed: 1)),
            ("子目录 🗂/存档 二号.sav", TestEnvironment.Bytes(768, seed: 2)));

        // 构造 > 260 字符的路径
        var deepRoot = Path.Combine(env.ProfileDir, "Saved Games", "DeepPath");
        var segment = "very-long-directory-name-segment";
        var current = deepRoot;
        for (var i = 0; i < 8; i++)
        {
            current = Path.Combine(current, $"{segment}-{i:D2}");
        }

        Directory.CreateDirectory(LongPath.Ensure(current));
        var deepFile = Path.Combine(current, "deep save 深.sav");
        File.WriteAllBytes(LongPath.Ensure(deepFile), TestEnvironment.Bytes(1024, seed: 3));

        Assert.True(deepFile.Length > 260, $"构造的路径不够长：{deepFile.Length}");

        var gameA = await env.AddGameAsync("Unicode", default, unicodeDir);
        var gameB = await env.AddGameAsync("Deep", default, deepRoot);

        var snapA = await env.SnapshotService.CreateAsync(gameA.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        var snapB = await env.SnapshotService.CreateAsync(gameB.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var expectedA = env.HashTree(unicodeDir);
        var expectedB = env.HashTree(deepRoot);

        // 破坏后再恢复
        File.WriteAllText(Path.Combine(unicodeDir, "存档 一号.sav"), "broken");
        File.WriteAllBytes(LongPath.Ensure(deepFile), new byte[10]);

        var planA = await env.RestoreService.PlanAsync(snapA.SnapshotId!.Value, CancellationToken.None);
        var planB = await env.RestoreService.PlanAsync(snapB.SnapshotId!.Value, CancellationToken.None);
        var resultA = await env.RestoreService.ExecuteAsync(planA, null, CancellationToken.None);
        var resultB = await env.RestoreService.ExecuteAsync(planB, null, CancellationToken.None);

        _output.WriteLine($"[A7] 中文/emoji 目录：{snapA.FileCount} 文件，恢复失败 {resultA.Failures.Count}；" +
                          $"超长路径（{deepFile.Length} 字符）：{snapB.FileCount} 文件，恢复失败 {resultB.Failures.Count}");

        Assert.True(resultA.Success, string.Join(";", resultA.Failures.Select(f => f.Reason)));
        Assert.True(resultB.Success, string.Join(";", resultB.Failures.Select(f => f.Reason)));
        TestEnvironment.AssertTreesEqual(expectedA, env.HashTree(unicodeDir));
        TestEnvironment.AssertTreesEqual(expectedB, env.HashTree(deepRoot));
    }

    [Fact]
    public async Task A8_ReadOnlyFileRestoredWithContentAndAttribute()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/A8",
            ("readonly.sav", TestEnvironment.Bytes(256, seed: 1)),
            ("normal.sav", TestEnvironment.Bytes(256, seed: 2)));

        var game = await env.AddGameAsync("A8", default, dir);
        var snapshot = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var readonlyPath = Path.Combine(dir, "readonly.sav");
        File.WriteAllBytes(readonlyPath, TestEnvironment.Bytes(256, seed: 42));
        File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);

        var plan = await env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        var result = await env.RestoreService.ExecuteAsync(plan, null, CancellationToken.None);

        var attributes = File.GetAttributes(readonlyPath);
        File.SetAttributes(readonlyPath, FileAttributes.Normal);

        _output.WriteLine($"[A8] 恢复成功={result.Success}，内容={TestEnvironment.HashFile(readonlyPath)[..12]}…，" +
                          $"属性={attributes}");

        Assert.True(result.Success);
        Assert.Equal(TestEnvironment.Bytes(256, seed: 1), File.ReadAllBytes(readonlyPath));
        Assert.True((attributes & FileAttributes.ReadOnly) != 0, "只读属性必须保留");
    }
}
