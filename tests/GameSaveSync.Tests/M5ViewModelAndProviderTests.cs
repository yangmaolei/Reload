using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// M5 第四批：ViewModel 展示逻辑、调度器、远端 Provider 适配。
/// </summary>
public class M5ViewModelAndProviderTests
{
    // ============================================================
    // 游戏卡片 ViewModel
    // ============================================================

    [Theory]
    [InlineData(GameSource.Steam, "Steam")]
    [InlineData(GameSource.Epic, "Epic")]
    [InlineData(GameSource.Gog, "GOG")]
    [InlineData(GameSource.Ubisoft, "Ubisoft")]
    [InlineData(GameSource.Uwp, "UWP")]
    [InlineData(GameSource.Manual, "手动")]
    public void GameItem_PlatformText_MapsKnownSources(GameSource source, string expected)
    {
        var vm = new GameListItemViewModel(new Game { Name = "G", Source = source });

        Assert.Equal(expected, vm.PlatformText);
    }

    [Fact]
    public void GameItem_Detail_ShowsRelocateHint()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G", NeedsRelocate = true });

        Assert.Equal("路径需重新定位", vm.Detail);
        Assert.Equal("存档目录需重新定位", vm.RootSummary);
    }

    [Fact]
    public void GameItem_RootSummary_ListsUpToTwoRoots()
    {
        var game = new Game { Name = "G" };
        game.SaveRoots.Add(new SaveRoot { Token = "%USERPROFILE%\\A" });
        game.SaveRoots.Add(new SaveRoot { Token = "%USERPROFILE%\\B" });
        game.SaveRoots.Add(new SaveRoot { Token = "%USERPROFILE%\\C" });

        var vm = new GameListItemViewModel(game);

        Assert.Contains("等 3 处", vm.RootSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void GameItem_RootSummary_TwoRootsHasNoSuffix()
    {
        var game = new Game { Name = "G" };
        game.SaveRoots.Add(new SaveRoot { Token = "%USERPROFILE%\\A" });
        game.SaveRoots.Add(new SaveRoot { Token = "%USERPROFILE%\\B" });

        var vm = new GameListItemViewModel(game);

        Assert.DoesNotContain("等", vm.RootSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameItem_Refresh_NoSnapshots_ShowsNeverBackedUp()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var game = await env.AddGameAsync("测试游戏");
        var vm = new GameListItemViewModel(game);

        await vm.RefreshAsync(env.Snapshots, env.IndexState, default);

        Assert.Equal("从未备份", vm.LastBackupText);
        Assert.Equal("未备份", vm.StateText);
        Assert.Equal(0, vm.SnapshotCount);
    }

    [Fact]
    public async Task GameItem_Refresh_WithSnapshot_ShowsPending()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Card", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("卡片游戏", default, dir);
        await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, default);

        var vm = new GameListItemViewModel(game);
        await vm.RefreshAsync(env.Snapshots, env.IndexState, default);

        Assert.Equal(1, vm.SnapshotCount);
        Assert.Equal("待上传", vm.StateText);
        Assert.Contains("1 个快照", vm.MetaText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GameItem_Refresh_NeedsRelocate_TakesPrecedence()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/CardReloc", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("重定位游戏", default, dir);
        await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, default);

        var vm = new GameListItemViewModel(game);
        vm.Model.NeedsRelocate = true;

        await vm.RefreshAsync(env.Snapshots, env.IndexState, default);

        Assert.Equal("需重新定位", vm.StateText);
    }

    [Fact]
    public void SnapshotItem_FileCountText_ShowsSkippedCount()
    {
        var vm = new SnapshotListItemViewModel(new Snapshot
        {
            FileCount = 5,
            SkippedCount = 2,
            CreatedAt = 1,
        });

        Assert.Equal("5 文件（跳过 2）", vm.FileCountText);
    }

    [Fact]
    public void SnapshotItem_NoSkipped_ShowsPlainCount()
    {
        var vm = new SnapshotListItemViewModel(new Snapshot { FileCount = 5, CreatedAt = 1 });

        Assert.Equal("5 文件", vm.FileCountText);
    }

    [Fact]
    public void SnapshotItem_ZeroTimestamp_ShowsPlaceholder()
    {
        var vm = new SnapshotListItemViewModel(new Snapshot { CreatedAt = 0 });

        Assert.Equal("--", vm.DateText);
        Assert.Equal("--:--", vm.TimeText);
    }

    // ============================================================
    // 调度器
    // ============================================================

    [Fact]
    public async Task Scheduler_StartTwice_IsIdempotent()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await env.Scheduler.StartAsync(default);
        await env.Scheduler.StartAsync(default);

        Assert.True(env.ProcessWatcher.IsStarted);
    }

    [Fact]
    public async Task Scheduler_StopWithoutStart_DoesNotThrow()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await env.Scheduler.StopAsync();
    }

    [Fact]
    public async Task Scheduler_PauseThenResume_Works()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        await env.Scheduler.StartAsync(default);
        env.Scheduler.Pause(TimeSpan.FromMinutes(30));
        Assert.True(env.Scheduler.IsPaused);

        env.Scheduler.Resume();
        Assert.False(env.Scheduler.IsPaused);
    }

    [Fact]
    public async Task Scheduler_GameExited_TriggersSnapshot()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/Sched", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("调度游戏", default, dir);

        await env.Scheduler.StartAsync(default);
        env.ProcessWatcher.RaiseStarted(game, 1234);
        env.ProcessWatcher.RaiseExited(game, 1234);

        // 退避延迟后应产生快照
        await Task.Delay(600);
        var snapshots = await env.Snapshots.ListByGameAsync(game.Id, default);
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public async Task Scheduler_Paused_SkipsCapture()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var dir = env.CreateSaveDir("Saved Games/SchedPause", ("a.dat", TestEnvironment.Bytes(16)));
        var game = await env.AddGameAsync("暂停游戏", default, dir);

        await env.Scheduler.StartAsync(default);
        env.Scheduler.Pause(TimeSpan.FromMinutes(30));

        env.ProcessWatcher.RaiseStarted(game, 1234);
        env.ProcessWatcher.RaiseExited(game, 1234);

        await Task.Delay(500);
        var snapshots = await env.Snapshots.ListByGameAsync(game.Id, default);
        Assert.Empty(snapshots);
    }

    // ============================================================
    // Provider 适配（分块 / 限流 / 工厂）
    // ============================================================

    [Fact]
    public async Task ProviderAdapter_SmallFile_UsesSinglePut()
    {
        await using var server = new InMemoryWebDavServer();

        var options = new GameSaveSync.WebDav.WebDavOptions
        {
            BaseUrl = server.BaseUrl,
            RemoteRoot = "GameSaveSync",
            Username = "tester",
            Password = "secret",
            UseProxy = false,
            ChunkSizeBytes = 1024 * 1024,
        };

        var client = new GameSaveSync.WebDav.WebDavClient(options);
        var adapter = GameSaveSync.WebDav.Providers.ProviderAdapterFactory.Create(options);

        var content = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(content);

        await adapter.PutObjectAsync(client, "objects/ab/abc123", stream, content.Length, default);

        Assert.NotNull(server.Get("GameSaveSync/objects/ab/abc123"));
    }

    [Fact]
    public async Task ProviderAdapter_Generic_UsesSinglePut()
    {
        await using var server = new InMemoryWebDavServer();

        var options = new GameSaveSync.WebDav.WebDavOptions
        {
            BaseUrl = server.BaseUrl,
            RemoteRoot = "GameSaveSync",
            Username = "tester",
            Password = "secret",
            UseProxy = false,
        };

        var client = new GameSaveSync.WebDav.WebDavClient(options);
        var adapter = GameSaveSync.WebDav.Providers.ProviderAdapterFactory.Create(options);

        var content = new byte[] { 9, 8, 7 };
        using var stream = new MemoryStream(content);
        await adapter.PutObjectAsync(client, "objects/de/def456", stream, content.Length, default);

        Assert.NotNull(server.Get("GameSaveSync/objects/de/def456"));
    }

    [Fact]
    public async Task ProviderAdapter_RateLimited_StillStoresContent()
    {
        await using var server = new InMemoryWebDavServer();

        var options = new GameSaveSync.WebDav.WebDavOptions
        {
            BaseUrl = server.BaseUrl,
            RemoteRoot = "GameSaveSync",
            Username = "tester",
            Password = "secret",
            UseProxy = false,
            MinRequestIntervalMs = 1,
        };

        var client = new GameSaveSync.WebDav.WebDavClient(options);
        var adapter = GameSaveSync.WebDav.Providers.ProviderAdapterFactory.Create(options);

        var content = new byte[] { 1 };
        using var stream = new MemoryStream(content);
        await adapter.PutObjectAsync(client, "objects/aa/aaa111", stream, content.Length, default);

        Assert.NotNull(server.Get("GameSaveSync/objects/aa/aaa111"));
    }

    // ============================================================
    // Outbox 载荷解析
    // ============================================================

    [Fact]
    public void OutboxPayloads_RoundTrip()
    {
        var gameId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        var objJson = OutboxPayloads.ForObject("abc");
        Assert.Equal("abc", OutboxPayloads.ParseObject(objJson).Hash);

        var manJson = OutboxPayloads.ForManifest(gameId, snapshotId, "f.json");
        var manifest = OutboxPayloads.ParseManifest(manJson);
        Assert.Equal(gameId, manifest.GameId);
        Assert.Equal(snapshotId, manifest.SnapshotId);
        Assert.Equal("f.json", manifest.FileName);
    }

    [Fact]
    public void OutboxPayloads_MalformedPayload_Throws()
    {
        Assert.ThrowsAny<Exception>(() => OutboxPayloads.ParseObject("not json"));
        Assert.ThrowsAny<Exception>(() => OutboxPayloads.ParseManifest("not json"));
    }

    [Fact]
    public void OutboxBackoff_GrowsAndCaps()
    {
        // 用固定随机源去掉 ±20% 抖动，断言退避曲线本身
        var fixedRandom = new Random(42);

        var first = OutboxBackoff.Compute(1, fixedRandom);
        var second = OutboxBackoff.Compute(2, fixedRandom);
        var third = OutboxBackoff.Compute(3, fixedRandom);
        var capped = OutboxBackoff.Compute(30, fixedRandom);

        Assert.True(second > first, "第 2 次退避应大于第 1 次");
        Assert.True(third > second, "第 3 次退避应大于第 2 次");
        Assert.True(capped.TotalSeconds <= OutboxBackoff.MaxDelay.TotalSeconds + 0.01,
            "退避必须封顶在 5 分钟");
        Assert.True(capped.TotalSeconds >= OutboxBackoff.MaxDelay.TotalSeconds * 0.8,
            "封顶值不应低于 5 分钟的 80%");
    }

    [Fact]
    public void OutboxBackoff_NeverBelowFloor()
    {
        var delay = OutboxBackoff.Compute(1);

        Assert.True(delay.TotalSeconds >= 0.2);
    }
}
