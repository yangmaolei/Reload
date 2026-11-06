using System.Net;
using System.Net.Sockets;
using System.Text;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;
using GameSaveSync.WebDav;

namespace GameSaveSync.Tests;

public sealed class WebDavSyncTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task TestConnection_CreatesDirectoryWritesProbeAndCleansUp()
    {
        var result = await _env.Remote!.TestConnectionDetailedAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(_env.Server!.HasCollection("GameSaveSync"));
        Assert.Empty(_env.Server.KeysWithPrefix("GameSaveSync/.probe/"));   // 探针必须删掉
    }

    [Fact]
    public async Task TestConnection_ReportsAuthenticationFailure()
    {
        _env.Server!.RequireAuth = true;
        _env.Server.ExpectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("tester:wrong"));

        var result = await _env.Remote!.TestConnectionDetailedAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("认证失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_PutsObjectsThenManifestAndMarksUploaded()
    {
        var dir = _env.CreateSaveDir("Saved Games/Upload", ("a.sav", TestEnvironment.Bytes(1024, seed: 4)));
        var game = await _env.AddGameAsync("Upload", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "上传测试", CancellationToken.None);

        var outcome = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(1, outcome.ObjectsUploaded);

        var reloaded = await _env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(RemoteSnapshotState.Uploaded, reloaded!.RemoteState);

        // 远端骨架齐全
        Assert.NotNull(_env.Server!.GetText("GameSaveSync/vault.json"));
        Assert.True(_env.Server.HasCollection("GameSaveSync/objects"));
        Assert.True(_env.Server.HasCollection("GameSaveSync/games"));

        // 对象按 hash 分桶
        var file = (await _env.Snapshots.GetFilesAsync(snapshot.SnapshotId!.Value, CancellationToken.None))[0];
        Assert.NotNull(_env.Server.Get($"GameSaveSync/objects/{file.Hash[..2]}/{file.Hash}"));

        // 清单可解析、路径是令牌化的
        var manifestKey = _env.Server.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/").Single();
        var manifest = SnapshotManifestSerializer.FromJson(_env.Server.GetText(manifestKey)!);
        Assert.Equal(snapshot.SnapshotId, manifest.SnapshotId);
        Assert.StartsWith("%SAVEDGAMES%", Assert.Single(manifest.Roots).Token);
        Assert.Equal("上传测试", manifest.Note);

        // 上传顺序：所有对象先于清单（清单是"临时名 PUT + MOVE"或直接 PUT）
        var log = _env.Server.RequestLog.ToList();
        var manifestUploadIndex = log.FindIndex(
            e => e.StartsWith("PUT GameSaveSync/games/", StringComparison.Ordinal) &&
                 e.Contains("/snapshots/", StringComparison.Ordinal));
        var objectIndexes = log
            .Select((entry, index) => (entry, index))
            .Where(x => x.entry.StartsWith("PUT GameSaveSync/objects/", StringComparison.Ordinal))
            .Select(x => x.index)
            .ToList();

        Assert.NotEmpty(objectIndexes);
        Assert.True(manifestUploadIndex >= 0, "未发现清单上传请求");
        Assert.True(objectIndexes.Max() < manifestUploadIndex, "清单必须在所有对象之后上传：\n" + string.Join("\n", log));
        Assert.Contains(log, e => e.StartsWith("MOVE GameSaveSync/games/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_FailureKeepsLocalSnapshotAndMarksPending()
    {
        var dir = _env.CreateSaveDir("Saved Games/Fail", ("a.sav", TestEnvironment.Bytes(2048, seed: 6)));
        var game = await _env.AddGameAsync("Fail", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        _env.Server!.FailPutStatus = 503;

        var outcome = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);

        var reloaded = await _env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(RemoteSnapshotState.Pending, reloaded!.RemoteState);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.LastError));

        // 本地快照与本地对象必须完好
        Assert.Equal(1, reloaded.FileCount);
        var files = await _env.Snapshots.GetFilesAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(_env.Store.Contains(files[0].Hash));
        Assert.Equal(1, await _env.Snapshots.CountAsync(game.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Upload_RetrySucceedsAfterServerRecovers()
    {
        var dir = _env.CreateSaveDir("Saved Games/Retry", ("a.sav", TestEnvironment.Bytes(512, seed: 8)));
        var game = await _env.AddGameAsync("Retry", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        _env.Server!.FailPutStatus = 500;
        var failed = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.False(failed.Success);

        _env.Server.FailPutStatus = 0;
        var retried = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.True(retried.Success, retried.Error);
        var reloaded = await _env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(RemoteSnapshotState.Uploaded, reloaded!.RemoteState);
        Assert.True(_env.Server.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/").Count == 1);
    }

    [Fact]
    public async Task Upload_NotConfiguredKeepsSnapshotPending()
    {
        var offlineEnv = TestEnvironment.Create(withWebDav: false);
        try
        {
            var dir = offlineEnv.CreateSaveDir("Saved Games/Offline", ("a.sav", TestEnvironment.Bytes(128)));
            var game = await offlineEnv.AddGameAsync("Offline", default, dir);
            var snapshot = await offlineEnv.SnapshotService.CreateAsync(
                game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

            var outcome = await offlineEnv.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Contains("尚未配置", outcome.Error!);
            var reloaded = await offlineEnv.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
            Assert.Equal(RemoteSnapshotState.Pending, reloaded!.RemoteState);
        }
        finally
        {
            await offlineEnv.DisposeAsync();
        }
    }

    [Fact]
    public async Task Upload_NetworkDownProducesPendingNotException()
    {
        var deadPort = GetFreeTcpPort();
        var options = new WebDavOptions
        {
            BaseUrl = $"http://127.0.0.1:{deadPort}/dav/",
            RemoteRoot = "GameSaveSync",
            TimeoutSeconds = 3,
            UseProxy = false,
        };

        using var client = new WebDavClient(options);
        // 连接被拒绝 → HttpRequestException，必须被 UploadService 捕获并标记 pending
        using var remote = new WebDavRemoteStore(options, _env.Logger);
        _ = client;

        var dir = _env.CreateSaveDir("Saved Games/DeadPort", ("a.sav", TestEnvironment.Bytes(256)));
        var game = await _env.AddGameAsync("DeadPort", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var outbox = new SqliteOutboxStore(_env.Database);
        var executor = new RemoteOperationExecutor(
            remote, _env.Store, _env.Objects, _env.Snapshots, _env.ManifestStore, _env.Settings,
            _env.IndexService, _env.Logger);
        var processor = new OutboxProcessor(outbox, executor, SystemClock.Instance, _env.Logger);
        var uploadService = new UploadService(
            _env.Snapshots, _env.Objects, _env.Store, remote, outbox, processor, _env.Logger);

        var outcome = await uploadService.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.False(outcome.Success);
        var reloaded = await _env.Snapshots.GetAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.Equal(RemoteSnapshotState.Pending, reloaded!.RemoteState);
        Assert.True(await _env.Snapshots.CountAsync(game.Id, CancellationToken.None) == 1);
    }

    [Fact]
    public async Task UploadPending_ResendsAllPendingSnapshots()
    {
        var dir = _env.CreateSaveDir("Saved Games/PendingAll", ("a.sav", TestEnvironment.Bytes(64, seed: 1)));
        var game = await _env.AddGameAsync("PendingAll", default, dir);

        _env.Server!.FailPutStatus = 500;
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(64, seed: i));
            var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
            await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        }

        Assert.Equal(3, (await _env.Snapshots.ListPendingAsync(CancellationToken.None)).Count);

        _env.Server.FailPutStatus = 0;
        var results = await _env.Uploads.UploadPendingAsync(CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success, r.Error));
        Assert.Empty(await _env.Snapshots.ListPendingAsync(CancellationToken.None));
        Assert.Equal(3, _env.Server.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/").Count);
    }

    [Theory]
    [InlineData("中文游戏/存档 目录")]
    [InlineData("emoji-🎮-save")]
    public async Task Upload_HandlesUnicodeSavePaths(string relativeDir)
    {
        var dir = _env.CreateSaveDir($"Saved Games/{relativeDir}", ("slot 1.sav", TestEnvironment.Bytes(128, seed: 3)));
        var game = await _env.AddGameAsync(relativeDir, default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var outcome = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var manifestKey = _env.Server!.KeysWithPrefix($"GameSaveSync/games/{game.Id:D}/snapshots/").Single();
        var manifest = SnapshotManifestSerializer.FromJson(_env.Server.GetText(manifestKey)!);
        Assert.Contains("%SAVEDGAMES%", manifest.Roots[0].Token);
        Assert.Equal("slot 1.sav", manifest.Files[0].RelPath);
    }

    [Fact]
    public async Task NextcloudProvider_UsesChunkedUploadAndLandsExactContent()
    {
        var options = _env.RemoteOptions.Clone();
        options.Provider = WebDavProvider.Nextcloud;
        options.ChunkSizeBytes = 256 * 1024;

        using var store = new WebDavRemoteStore(options, _env.Logger);
        var content = TestEnvironment.Bytes(1_000_000, seed: 3);
        var hash = TestEnvironment.HashFileWith(content);

        await store.PutObjectAsync(hash, new MemoryStream(content), CancellationToken.None);

        var stored = _env.Server!.Get($"GameSaveSync/objects/{hash[..2]}/{hash}");
        var usedChunking = _env.Server.RequestLog.Any(r => r.Contains("-chunking-", StringComparison.Ordinal));
        var movedIntoPlace = _env.Server.RequestLog.Any(r => r.StartsWith("MOVE GameSaveSync/objects/", StringComparison.Ordinal));

        Assert.NotNull(stored);
        Assert.Equal(content, stored);
        Assert.True(usedChunking, "Nextcloud 适配器应走分块上传");
        Assert.True(movedIntoPlace, "分块上传应以 MOVE 收尾");
    }

    [Fact]
    public async Task GenericProvider_UploadsInOnePut()
    {
        var options = _env.RemoteOptions.Clone();
        options.Provider = WebDavProvider.Generic;

        using var store = new WebDavRemoteStore(options, _env.Logger);
        var content = TestEnvironment.Bytes(512 * 1024, seed: 4);
        var hash = TestEnvironment.HashFileWith(content);

        await store.PutObjectAsync(hash, new MemoryStream(content), CancellationToken.None);

        Assert.NotNull(_env.Server!.Get($"GameSaveSync/objects/{hash[..2]}/{hash}"));
        Assert.DoesNotContain(_env.Server.RequestLog, r => r.Contains("-chunking-", StringComparison.Ordinal));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
