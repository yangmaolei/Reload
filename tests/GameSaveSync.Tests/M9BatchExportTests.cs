using GameSaveSync.Core.Export;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Storage;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// 批量备份 / 导出：目录导出与 zip 打包。
/// 覆盖：目录结构清晰、同名游戏不互相覆盖、include/exclude 规则生效、
/// 目标已存在的处理、单个游戏失败不影响其他游戏、空结果。
/// </summary>
public sealed class M7BatchExportTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _env.DisposeAsync().AsTask();

    private BatchSaveExporter NewExporter() => new(
        _env.Games,
        _env.FileSystem,
        _env.Tokens,
        _env.Logger);

    // ============================================================
    // 一、导出到目录
    // ============================================================

    [Fact]
    public async Task ExportToDirectory_CreatesSubfolderPerGame()
    {
        var dirA = _env.CreateSaveDir("Saved Games/Alpha", ("a.sav", TestEnvironment.Bytes(64, 1)));
        var dirB = _env.CreateSaveDir("Saved Games/Beta", ("b.sav", TestEnvironment.Bytes(32, 2)));

        var gameA = await _env.AddGameAsync("Alpha", default, dirA);
        var gameB = await _env.AddGameAsync("Beta", default, dirB);

        var target = Path.Combine(_env.Root, "backup-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { gameA.Id, gameB.Id }, target, overwriteExisting: false, default);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(2, result.TotalFileCount);

        // 每个游戏一个独立子目录，名字带 ID 前 8 位
        var folders = Directory.GetDirectories(target);
        Assert.Equal(2, folders.Length);
        Assert.Contains(folders, f => Path.GetFileName(f).StartsWith("Alpha_", StringComparison.Ordinal));
        Assert.Contains(folders, f => Path.GetFileName(f).StartsWith("Beta_", StringComparison.Ordinal));

        // 文件内容原样保留
        var alphaFolder = folders.Single(f => Path.GetFileName(f).StartsWith("Alpha_", StringComparison.Ordinal));
        var files = Directory.GetFiles(alphaFolder, "*.sav", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.Equal(TestEnvironment.Bytes(64, 1), await File.ReadAllBytesAsync(files[0]));
    }

    [Fact]
    public async Task ExportToDirectory_SameNameGames_DoNotOverwriteEachOther()
    {
        var dirA = _env.CreateSaveDir("Saved Games/Dup1", ("x.sav", TestEnvironment.Bytes(10, 1)));
        var dirB = _env.CreateSaveDir("Saved Games/Dup2", ("x.sav", TestEnvironment.Bytes(20, 2)));

        var gameA = await _env.AddGameAsync("同名游戏", default, dirA);
        var gameB = await _env.AddGameAsync("同名游戏", default, dirB);

        var target = Path.Combine(_env.Root, "dup-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { gameA.Id, gameB.Id }, target, overwriteExisting: false, default);

        Assert.True(result.AllSucceeded);

        // 两个同名游戏 → 两个不同目录（靠 ID 区分）
        var folders = Directory.GetDirectories(target);
        Assert.Equal(2, folders.Length);
        Assert.NotEqual(Path.GetFileName(folders[0]), Path.GetFileName(folders[1]));
    }

    [Fact]
    public async Task ExportToDirectory_AppliesExcludeRules()
    {
        var dir = _env.CreateSaveDir(
            "Saved Games/Filtered",
            ("keep.sav", TestEnvironment.Bytes(8, 1)),
            ("skip.log", TestEnvironment.Bytes(4, 2)));

        var game = await _env.AddGameAsync("Filtered", default, dir);
        // 给这个游戏加一条排除规则
        var model = await _env.Games.GetAsync(game.Id, default);
        model!.SaveRoots[0].Exclude.Add("*.log");
        await _env.Games.UpsertAsync(model, default);

        var target = Path.Combine(_env.Root, "filtered-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.TotalFileCount);

        var exported = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
        Assert.Single(exported);
        Assert.EndsWith("keep.sav", exported[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToDirectory_TargetExists_WithoutOverwrite_ReportsFailure()
    {
        var dir = _env.CreateSaveDir("Saved Games/Exist", ("a.sav", TestEnvironment.Bytes(8, 1)));
        var game = await _env.AddGameAsync("Exist", default, dir);

        var target = Path.Combine(_env.Root, "exists-out");

        // 第一次成功
        var first = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);
        Assert.True(first.AllSucceeded);

        // 第二次不覆盖 → 该项失败并说明原因，不能静默混进旧内容
        var second = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);

        Assert.False(second.AllSucceeded);
        Assert.Equal(1, second.FailureCount);
        Assert.Contains("已存在", second.Items[0].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToDirectory_TargetExists_WithOverwrite_Succeeds()
    {
        var dir = _env.CreateSaveDir("Saved Games/Ov", ("a.sav", TestEnvironment.Bytes(8, 1)));
        var game = await _env.AddGameAsync("Ov", default, dir);

        var target = Path.Combine(_env.Root, "overwrite-out");

        await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);
        var second = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: true, default);

        Assert.True(second.AllSucceeded);
        Assert.Equal(1, second.TotalFileCount);
    }

    [Fact]
    public async Task ExportToDirectory_OneGameFails_OthersStillSucceed()
    {
        var goodDir = _env.CreateSaveDir("Saved Games/Good", ("a.sav", TestEnvironment.Bytes(8, 1)));
        var good = await _env.AddGameAsync("Good", default, goodDir);

        // 指向一个不存在的目录 → 该游戏无可用存档根
        var bad = await _env.AddGameAsync(
            "Bad",
            default,
            Path.Combine(_env.ProfileDir, "does-not-exist"));

        var target = Path.Combine(_env.Root, "mixed-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { good.Id, bad.Id }, target, overwriteExisting: false, default);

        // 坏游戏（存档目录不存在）明确报失败，好游戏照样成功
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(1, result.TotalFileCount);
        Assert.Contains(result.Items, i => i.GameId == good.Id && i.Success);
        Assert.Contains(result.Items, i => i.GameId == bad.Id && !i.Success);
    }

    [Fact]
    public async Task ExportToDirectory_NonExistentGame_ReportsFailure()
    {
        var target = Path.Combine(_env.Root, "missing-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { Guid.NewGuid() }, target, overwriteExisting: false, default);

        Assert.False(result.AllSucceeded);
        Assert.Contains("不存在", result.Items[0].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToDirectory_EmptySelection_ReturnsEmptyResult()
    {
        var target = Path.Combine(_env.Root, "empty-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            Array.Empty<Guid>(), target, overwriteExisting: false, default);

        Assert.Empty(result.Items);
        Assert.True(result.AllSucceeded);
        Assert.Contains("没有", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToDirectory_SanitizesIllegalFileNameChars()
    {
        var dir = _env.CreateSaveDir("Saved Games/San", ("a.sav", TestEnvironment.Bytes(4, 1)));
        var game = await _env.AddGameAsync("bad/name:with*chars", default, dir);

        var target = Path.Combine(_env.Root, "san-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);

        Assert.True(result.AllSucceeded);

        var folder = Directory.GetDirectories(target).Single();
        var name = Path.GetFileName(folder);

        // 文件名里不能出现非法字符，也不能是路径穿越
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
    }

    // ============================================================
    // 二、导出到 zip
    // ============================================================

    [Fact]
    public async Task ExportToZip_CreatesEntriesGroupedByGame()
    {
        var dirA = _env.CreateSaveDir("Saved Games/ZA", ("a.sav", TestEnvironment.Bytes(64, 1)));
        var dirB = _env.CreateSaveDir("Saved Games/ZB", ("b.sav", TestEnvironment.Bytes(32, 2)));

        var gameA = await _env.AddGameAsync("ZipA", default, dirA);
        var gameB = await _env.AddGameAsync("ZipB", default, dirB);

        var zipPath = Path.Combine(_env.Root, "export.zip");
        var result = await NewExporter().ExportToZipAsync(
            new[] { gameA.Id, gameB.Id }, zipPath, default);

        Assert.True(result.AllSucceeded);
        Assert.True(File.Exists(zipPath));
        Assert.Equal(2, result.TotalFileCount);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.StartsWith("ZipA_", StringComparison.Ordinal) && e.EndsWith("a.sav", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.StartsWith("ZipB_", StringComparison.Ordinal) && e.EndsWith("b.sav", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportToZip_PreservesFileContent()
    {
        var content = TestEnvironment.Bytes(128, 7);
        var dir = _env.CreateSaveDir("Saved Games/ZipContent", ("save.dat", content));
        var game = await _env.AddGameAsync("ZipContent", default, dir);

        var zipPath = Path.Combine(_env.Root, "content.zip");
        await NewExporter().ExportToZipAsync(new[] { game.Id }, zipPath, default);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.Single();

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        Assert.Equal(content, memory.ToArray());
    }

    [Fact]
    public async Task ExportToZip_ExistingFile_IsOverwritten()
    {
        var dir = _env.CreateSaveDir("Saved Games/ZipOver", ("a.sav", TestEnvironment.Bytes(8, 1)));
        var game = await _env.AddGameAsync("ZipOver", default, dir);

        var zipPath = Path.Combine(_env.Root, "overwrite.zip");
        await File.WriteAllTextAsync(zipPath, "旧内容");

        var result = await NewExporter().ExportToZipAsync(new[] { game.Id }, zipPath, default);

        Assert.True(result.AllSucceeded);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public async Task ExportToZip_NonExistentGame_ReportsFailure()
    {
        var zipPath = Path.Combine(_env.Root, "missing.zip");
        var result = await NewExporter().ExportToZipAsync(new[] { Guid.NewGuid() }, zipPath, default);

        Assert.False(result.AllSucceeded);
        Assert.Contains("不存在", result.Items[0].Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportToZip_AppliesExcludeRules()
    {
        var dir = _env.CreateSaveDir(
            "Saved Games/ZipFilter",
            ("keep.sav", TestEnvironment.Bytes(8, 1)),
            ("skip.log", TestEnvironment.Bytes(4, 2)));

        var game = await _env.AddGameAsync("ZipFilter", default, dir);
        var model = await _env.Games.GetAsync(game.Id, default);
        model!.SaveRoots[0].Exclude.Add("*.log");
        await _env.Games.UpsertAsync(model, default);

        var zipPath = Path.Combine(_env.Root, "filtered.zip");
        var result = await NewExporter().ExportToZipAsync(new[] { game.Id }, zipPath, default);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.TotalFileCount);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        Assert.Single(archive.Entries);
    }

    // ============================================================
    // 三、结果模型
    // ============================================================

    [Fact]
    public void BatchExportResult_Summary_ReportsSuccessAndFailure()
    {
        var items = new[]
        {
            new BatchExportItem(Guid.NewGuid(), "A", true, 3, 100, null),
            new BatchExportItem(Guid.NewGuid(), "B", false, 0, 0, "路径不存在"),
        };

        var result = new BatchExportResult(@"C:\out", items);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(3, result.TotalFileCount);
        Assert.Equal(100, result.TotalBytes);
        Assert.False(result.AllSucceeded);
        Assert.Contains("成功 1/2", result.Summary, StringComparison.Ordinal);
        Assert.Contains("失败 1", result.Summary, StringComparison.Ordinal);

        // 逐项摘要要能看出成功与失败原因
        Assert.Contains("3 个文件", items[0].Summary, StringComparison.Ordinal);
        Assert.Contains("路径不存在", items[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchSaveExporter_Sanitize_ReplacesIllegalChars()
    {
        Assert.Equal("a_b_c", BatchSaveExporter.Sanitize("a/b\\c"));
        Assert.Equal(string.Empty, BatchSaveExporter.Sanitize("   "));
        Assert.Equal("normal", BatchSaveExporter.Sanitize("normal"));
    }

    // ============================================================
    // 四、目录导出与 zip 导出的语义一致性
    // ============================================================

    /// <summary>
    /// 「一键备份」和「一键导出」是同一批数据的两种呈现：
    /// 前者落成目录树，后者打成 zip。两者选中的文件集合必须完全一致，
    /// 否则用户会发现"备份出来的和导出的对不上"。
    /// </summary>
    [Fact]
    public async Task DirectoryExport_AndZipExport_CoverTheSameFiles()
    {
        var dirA = _env.CreateSaveDir(
            "Saved Games/ConsistA",
            ("a1.sav", TestEnvironment.Bytes(16, 1)),
            ("a2.sav", TestEnvironment.Bytes(16, 2)),
            ("sub/a3.sav", TestEnvironment.Bytes(16, 3)));
        var dirB = _env.CreateSaveDir(
            "Saved Games/ConsistB",
            ("b1.sav", TestEnvironment.Bytes(16, 4)));

        var gameA = await _env.AddGameAsync("ConsistA", default, dirA);
        var gameB = await _env.AddGameAsync("ConsistB", default, dirB);

        var ids = new[] { gameA.Id, gameB.Id };
        var exporter = NewExporter();

        var dirTarget = Path.Combine(_env.Root, "consist-dir");
        var dirResult = await exporter.ExportToDirectoryAsync(ids, dirTarget, true, default);

        var zipPath = Path.Combine(_env.Root, "consist.zip");
        var zipResult = await exporter.ExportToZipAsync(ids, zipPath, default);

        // 两边的成功/失败数与文件总数必须一致
        Assert.Equal(dirResult.SuccessCount, zipResult.SuccessCount);
        Assert.Equal(dirResult.FailureCount, zipResult.FailureCount);
        Assert.Equal(dirResult.TotalFileCount, zipResult.TotalFileCount);
        Assert.Equal(4, dirResult.TotalFileCount);

        // 目录里的文件相对路径集合 == zip 条目去掉"游戏名_xxx/存档根名/"前缀后的集合
        var dirFiles = Directory
            .GetFiles(dirTarget, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetFileName(p))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var zipFiles = archive.Entries
            .Select(e => Path.GetFileName(e.FullName))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(dirFiles, zipFiles);
    }

    /// <summary>目录导出后，每个游戏的存档文件都能在对应子目录里找到原文件。</summary>
    [Fact]
    public async Task DirectoryExport_PreservesOriginalFileBytes()
    {
        var content = TestEnvironment.Bytes(200, 9);
        var dir = _env.CreateSaveDir("Saved Games/Bytes", ("data.sav", content));
        var game = await _env.AddGameAsync("Bytes", default, dir);

        var target = Path.Combine(_env.Root, "bytes-out");
        var result = await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);

        Assert.True(result.AllSucceeded);

        var exported = Directory.GetFiles(target, "data.sav", SearchOption.AllDirectories);
        Assert.Single(exported);
        Assert.Equal(content, await File.ReadAllBytesAsync(exported[0]));
    }

    /// <summary>多层子目录的相对结构要原样保留，不能被拍平。</summary>
    [Fact]
    public async Task DirectoryExport_PreservesNestedDirectoryStructure()
    {
        var dir = _env.CreateSaveDir(
            "Saved Games/Nested",
            ("root.sav", TestEnvironment.Bytes(8, 1)),
            ("a/b/c/deep.sav", TestEnvironment.Bytes(8, 2)));

        var game = await _env.AddGameAsync("Nested", default, dir);

        var target = Path.Combine(_env.Root, "nested-out");
        await NewExporter().ExportToDirectoryAsync(
            new[] { game.Id }, target, overwriteExisting: false, default);

        var deep = Directory.GetFiles(target, "deep.sav", SearchOption.AllDirectories);
        Assert.Single(deep);

        // deep.sav 必须还在 a/b/c/ 之下
        var rel = deep[0].Substring(target.Length).Replace('/', '\\');
        Assert.Contains("a\\b\\c", rel, StringComparison.OrdinalIgnoreCase);
    }
}
