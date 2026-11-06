using GameSaveSync.Core.Storage;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

public sealed class ObjectStoreTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task StageAndCommit_WritesContentAddressedLayout()
    {
        var dir = _env.CreateSaveDir("saves/cas", ("a.sav", TestEnvironment.Bytes(1024)));
        var source = Path.Combine(dir, "a.sav");

        using var staged = await _env.Store.StageFromFileAsync(source, null, CancellationToken.None);
        Assert.True(_env.Store.Commit(staged));

        var expected = TestEnvironment.HashFile(source);
        Assert.Equal(expected, staged.Hash);

        var objectPath = Path.Combine(_env.DataRoot, "objects", expected[..2], expected);
        Assert.True(File.Exists(objectPath), $"CAS 路径不存在：{objectPath}");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(objectPath));

        // 暂存目录必须清空（rename 之后不留 tmp）
        Assert.Empty(Directory.GetFiles(Path.Combine(_env.DataRoot, "objects", "tmp")));
    }

    [Fact]
    public async Task Commit_IsIdempotentForSameContent()
    {
        var dir = _env.CreateSaveDir("saves/cas2",
            ("a.sav", TestEnvironment.Bytes(2048)),
            ("b.sav", TestEnvironment.Bytes(2048)));

        using var first = await _env.Store.StageFromFileAsync(Path.Combine(dir, "a.sav"), null, CancellationToken.None);
        using var second = await _env.Store.StageFromFileAsync(Path.Combine(dir, "b.sav"), null, CancellationToken.None);

        Assert.True(_env.Store.Commit(first));
        Assert.False(_env.Store.Commit(second));   // 内容相同 → 不重复写
        Assert.Equal(first.Hash, second.Hash);

        var bucket = Path.Combine(_env.DataRoot, "objects", first.Hash[..2]);
        Assert.Single(Directory.GetFiles(bucket));
    }

    [Fact]
    public async Task PutAsync_RejectsHashMismatchAndLeavesNoTempFile()
    {
        await using var stream = new MemoryStream(TestEnvironment.Bytes(512));
        var wrongHash = new string('a', 64);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _env.Store.PutAsync(wrongHash, stream, CancellationToken.None));

        Assert.False(_env.Store.Contains(wrongHash));
        Assert.Empty(Directory.GetFiles(Path.Combine(_env.DataRoot, "objects", "tmp")));
    }

    [Fact]
    public async Task PutAsync_AcceptsMatchingHash()
    {
        var content = TestEnvironment.Bytes(777, seed: 5);
        var hash = TestEnvironment.HashFileWith(content);

        await _env.Store.PutAsync(hash, new MemoryStream(content), CancellationToken.None);

        Assert.True(_env.Store.Contains(hash));
        using var stored = _env.Store.OpenReadAsync(hash, CancellationToken.None);
        using var memory = new MemoryStream();
        await stored.CopyToAsync(memory);
        Assert.Equal(content, memory.ToArray());
    }

    [Fact]
    public void CleanupTemporaryFiles_RemovesCrashLeftovers()
    {
        // 模拟"写到一半被强杀"：tmp 目录里残留 .part
        var tempDir = Path.Combine(_env.DataRoot, "objects", "tmp");
        var leftover = Path.Combine(tempDir, $"{Guid.NewGuid():N}.part");
        File.WriteAllBytes(leftover, TestEnvironment.Bytes(4096));

        var removed = _env.Store.CleanupTemporaryFiles();

        Assert.Equal(1, removed);
        Assert.False(File.Exists(leftover));
        Assert.Empty(Directory.GetFiles(tempDir));
    }

    [Fact]
    public void OpenRead_ThrowsForMissingObject()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _env.Store.OpenReadAsync(new string('b', 64), CancellationToken.None));
    }

    [Fact]
    public async Task StagedObject_DisposeWithoutCommitDeletesTemp()
    {
        var dir = _env.CreateSaveDir("saves/cas3", ("a.sav", TestEnvironment.Bytes(64)));
        var staged = await _env.Store.StageFromFileAsync(
            Path.Combine(dir, "a.sav"), null, CancellationToken.None);

        var tempDir = Path.Combine(_env.DataRoot, "objects", "tmp");
        Assert.Single(Directory.GetFiles(tempDir));

        staged.Dispose();

        Assert.Empty(Directory.GetFiles(tempDir));
        Assert.False(_env.Store.Contains(staged.Hash));
    }
}
