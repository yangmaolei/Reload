using GameSaveSync.Core.Models;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>index.json 合并状态机的单测（对应方案里"冲突合并规则"的四条分支）。</summary>
public sealed class M3IndexTests : IAsyncLifetime
{
    private TestEnvironment _env = null!;

    public Task InitializeAsync()
    {
        _env = TestEnvironment.Create(withWebDav: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private static IndexGameEntry Entry(long seq, Guid snapshot, string device, bool conflict = false) => new()
    {
        Seq = seq,
        SnapshotId = snapshot,
        DeviceId = device,
        UpdatedAt = 1,
        Conflict = conflict,
    };

    private static IndexMergeResult Merge(
        IndexDocument remote,
        IReadOnlyDictionary<string, IndexGameEntry> local,
        string localDevice,
        Func<Guid, Guid, bool>? related = null,
        Func<Guid, long?>? createdAt = null) =>
        IndexMerger.Merge(new IndexMergeRequest
        {
            Remote = remote,
            Local = local,
            LocalDeviceId = localDevice,
            AreRelated = related ?? ((_, _) => false),
            CreatedAtOf = createdAt ?? (_ => 0),
            NowMs = 100,
        });

    [Fact]
    public void Rule1_OnlyRemote_WinsRemote()
    {
        var snapshot = Guid.NewGuid();
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(3, snapshot, "device-b");

        var result = Merge(remote, new Dictionary<string, IndexGameEntry>(), "device-a");

        Assert.Equal(snapshot, result.Merged.Games["g1"].SnapshotId);
        Assert.Equal("device-b", result.Merged.Games["g1"].DeviceId);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Rule2_OnlyLocal_WinsLocal()
    {
        var snapshot = Guid.NewGuid();
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(2, snapshot, "device-a") };

        var result = Merge(new IndexDocument(), local, "device-a");

        Assert.Equal(snapshot, result.Merged.Games["g1"].SnapshotId);
        Assert.Equal("device-a", result.Merged.Games["g1"].DeviceId);
    }

    [Fact]
    public void Rule3_SameDevice_HigherSeqWins()
    {
        var localSnap = Guid.NewGuid();
        var remoteSnap = Guid.NewGuid();
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(4, remoteSnap, "device-a");
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(7, localSnap, "device-a") };

        var higher = Merge(remote, local, "device-a");
        Assert.Equal(localSnap, higher.Merged.Games["g1"].SnapshotId);

        var lowerLocal = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(2, localSnap, "device-a") };
        var lower = Merge(remote, lowerLocal, "device-a");
        Assert.Equal(remoteSnap, lower.Merged.Games["g1"].SnapshotId);
    }

    [Fact]
    public void Rule4_LocalHigherAndRelated_NormalAdvanceWithoutConflict()
    {
        var localSnap = Guid.NewGuid();
        var remoteSnap = Guid.NewGuid();
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(2, remoteSnap, "device-b");
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(5, localSnap, "device-a") };

        var result = Merge(remote, local, "device-a", (a, b) => a == localSnap && b == remoteSnap);

        Assert.Equal(localSnap, result.Merged.Games["g1"].SnapshotId);
        Assert.False(result.Merged.Games["g1"].Conflict);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Rule5_LocalHigherButForked_MarksConflictAndKeepsNewerSnapshotId()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(2, older, "device-b");
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(9, newer, "device-a") };

        var result = Merge(
            remote, local, "device-a",
            related: (_, _) => false,
            createdAt: id => id == newer ? 2000 : 1000);

        var entry = result.Merged.Games["g1"];
        Assert.True(entry.Conflict, "分叉必须标记冲突");
        Assert.Equal(newer, entry.SnapshotId);
        Assert.Equal(9, entry.Seq);
        Assert.Single(result.Conflicts);
        Assert.Equal("device-a", result.Conflicts[0].Local.DeviceId);
        Assert.Equal("device-b", result.Conflicts[0].Remote.DeviceId);

        // 反向：远端更新时，默认推荐远端，但仍然标记冲突
        var reversed = Merge(
            remote, local, "device-a",
            related: (_, _) => false,
            createdAt: id => id == newer ? 500 : 1000);
        Assert.Equal(older, reversed.Merged.Games["g1"].SnapshotId);
        Assert.True(reversed.Merged.Games["g1"].Conflict);
    }

    [Fact]
    public void Rule6_RemoteSeqHigherOrEqual_WinsRemote()
    {
        var localSnap = Guid.NewGuid();
        var remoteSnap = Guid.NewGuid();
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(6, remoteSnap, "device-b");
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(6, localSnap, "device-a") };

        var result = Merge(remote, local, "device-a");

        Assert.Equal(remoteSnap, result.Merged.Games["g1"].SnapshotId);
        Assert.Equal("device-b", result.Merged.Games["g1"].DeviceId);
    }

    [Fact]
    public void RemoteConflictFlagSurvivesWhenRemoteWins()
    {
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(8, Guid.NewGuid(), "device-b", conflict: true);
        var local = new Dictionary<string, IndexGameEntry> { ["g1"] = Entry(1, Guid.NewGuid(), "device-a") };

        var result = Merge(remote, local, "device-a");

        Assert.True(result.Merged.Games["g1"].Conflict);
    }

    [Fact]
    public void MergeKeepsUnionOfAllGames()
    {
        var remote = new IndexDocument();
        remote.Games["g1"] = Entry(1, Guid.NewGuid(), "device-b");
        remote.Games["g2"] = Entry(5, Guid.NewGuid(), "device-b");

        var local = new Dictionary<string, IndexGameEntry>
        {
            ["g1"] = Entry(2, Guid.NewGuid(), "device-a"),
            ["g3"] = Entry(1, Guid.NewGuid(), "device-a"),
        };

        var result = Merge(remote, local, "device-a");

        Assert.Equal(3, result.Merged.Games.Count);
        Assert.Contains("g1", result.Merged.Games.Keys);
        Assert.Contains("g2", result.Merged.Games.Keys);
        Assert.Contains("g3", result.Merged.Games.Keys);
    }

    [Fact]
    public void Serializer_RoundTripsAndRejectsGarbage()
    {
        var document = new IndexDocument { UpdatedAt = 123 };
        document.Games["g1"] = Entry(4, Guid.NewGuid(), "device-a", conflict: true);

        var json = IndexSerializer.ToJson(document);
        var parsed = IndexSerializer.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(4, parsed!.Games["g1"].Seq);
        Assert.True(parsed.Games["g1"].Conflict);

        Assert.Null(IndexSerializer.TryParse("{ not json"));
        Assert.Null(IndexSerializer.TryParse(null));
        Assert.Null(IndexSerializer.TryParse("[]"));
    }

    [Fact]
    public async Task Publish_WritesIndexAndIncrementsSeq()
    {
        var dir = _env.CreateSaveDir("Saved Games/IndexPublish", ("a.sav", TestEnvironment.Bytes(256)));
        var game = await _env.AddGameAsync("IndexPublish", default, dir);

        var first = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        await _env.Uploads.UploadSnapshotAsync(first.SnapshotId!.Value, CancellationToken.None);

        var json = _env.Server!.GetText("GameSaveSync/index.json");
        Assert.NotNull(json);
        var index = IndexSerializer.TryParse(json)!;
        var entry = index.Games[game.Id.ToString("D")];
        Assert.Equal(first.SnapshotId, entry.SnapshotId);
        Assert.Equal(1, entry.Seq);
        Assert.False(entry.Conflict);

        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(256, seed: 9));
        var second = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        await _env.Uploads.UploadSnapshotAsync(second.SnapshotId!.Value, CancellationToken.None);

        var index2 = IndexSerializer.TryParse(_env.Server.GetText("GameSaveSync/index.json"))!;
        Assert.Equal(second.SnapshotId, index2.Games[game.Id.ToString("D")].SnapshotId);
        Assert.Equal(2, index2.Games[game.Id.ToString("D")].Seq);
    }

    [Fact]
    public async Task Pull_UsesIfNoneMatchAndGetsNotModified()
    {
        var dir = _env.CreateSaveDir("Saved Games/IndexPull", ("a.sav", TestEnvironment.Bytes(128)));
        var game = await _env.AddGameAsync("IndexPull", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);
        await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);

        // 刚发布完索引，本机手里就有最新 ETag：两次轮询都应命中 304（几乎零成本）
        var logStart = _env.Server!.RequestLog.Count;
        var first = await _env.Poller.PollOnceAsync(CancellationToken.None);
        var second = await _env.Poller.PollOnceAsync(CancellationToken.None);
        Assert.False(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(2, _env.Poller.NotModifiedCount);

        var indexGets = _env.Server.RequestLog
            .Skip(logStart)
            .Count(r => r == "GET GameSaveSync/index.json");
        Assert.Equal(2, indexGets);

        // 换一台"新设备"（没有缓存 ETag）来拉：必须认为有变化
        await using var second2 = TestEnvironment.CreateSecondDevice(_env.Server);
        var pull = await second2.IndexService.PullAsync(CancellationToken.None);
        Assert.True(pull.Changed);
        Assert.True(pull.ImportedSnapshots >= 1);
    }

    [Fact]
    public async Task Publish_RetriesAfter412AndKeepsBothGames()
    {
        var dir = _env.CreateSaveDir("Saved Games/Index412", ("a.sav", TestEnvironment.Bytes(128)));
        var game = await _env.AddGameAsync("Index412", default, dir);
        var snapshot = await _env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        // 先造一个"别的设备刚写进来的游戏"
        var otherGame = Guid.NewGuid();
        var otherDoc = new IndexDocument
        {
            UpdatedAt = 1,
            Games =
            {
                [otherGame.ToString("D")] = new IndexGameEntry
                {
                    Seq = 3,
                    SnapshotId = Guid.NewGuid(),
                    DeviceId = "other-device",
                    UpdatedAt = 1,
                },
            },
        };
        _env.Server!.Overwrite("GameSaveSync/index.json",
            System.Text.Encoding.UTF8.GetBytes(IndexSerializer.ToJson(otherDoc)));

        // 注入一次 412：模拟"GET 之后、PUT 之前别的设备改了索引"
        _env.Server.FailIndexPutOnce = 1;

        var outcome = await _env.Uploads.UploadSnapshotAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var index = IndexSerializer.TryParse(_env.Server.GetText("GameSaveSync/index.json"))!;
        Assert.True(
            index.Games.Count == 2,
            $"索引内容：{_env.Server.GetText("GameSaveSync/index.json")}\n请求日志：{string.Join(" | ", _env.Server.RequestLog)}");
        Assert.True(index.Games.ContainsKey(otherGame.ToString("D")), "412 合并不能丢掉别的设备的数据");
        Assert.True(index.Games.ContainsKey(game.Id.ToString("D")));

        // 412 之后必须重新读一次（GET 次数 ≥ 2）
        var indexGets = _env.Server.RequestLog.Count(r => r == "GET GameSaveSync/index.json");
        Assert.True(indexGets >= 2, $"412 后应重新读取索引，实际 GET {indexGets} 次");
    }
}
