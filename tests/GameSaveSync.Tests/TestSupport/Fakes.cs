using GameSaveSync.Core.Models;
using GameSaveSync.Core.Scheduling;

namespace GameSaveSync.Tests.TestSupport;

/// <summary>可控的进程事件源：测试里直接 RaiseStarted/RaiseExited，不必真的起游戏。</summary>
public sealed class FakeProcessWatcher : IProcessWatcher
{
    private IReadOnlyList<Game> _games = Array.Empty<Game>();

    public event Action<Game, int>? GameStarted;

    public event Action<Game, int>? GameExited;

    public bool IsStarted { get; private set; }

    public IReadOnlyList<Game> Games => _games;

    public void Start(IReadOnlyList<Game> games)
    {
        _games = games.ToList();
        IsStarted = true;
    }

    public void UpdateGames(IReadOnlyList<Game> games) => _games = games.ToList();

    public void Stop() => IsStarted = false;

    public void RaiseStarted(Game game, int pid = 4242) => GameStarted?.Invoke(game, pid);

    public void RaiseExited(Game game, int pid = 4242) => GameExited?.Invoke(game, pid);

    public void Dispose() => Stop();
}

/// <summary>可控的目录变更事件源。</summary>
public sealed class FakeFileChangeWatcher : IFileChangeWatcher
{
    public event Action<Guid, bool>? Changed;

    public bool IsStarted { get; private set; }

    public void Start(IReadOnlyList<Game> games) => IsStarted = true;

    public void UpdateGames(IReadOnlyList<Game> games)
    {
    }

    public void Stop() => IsStarted = false;

    public void RaiseChanged(Guid gameId, bool overflow = false) => Changed?.Invoke(gameId, overflow);

    public void Dispose() => Stop();
}

/// <summary>可控的资源探针（idle 条件）。</summary>
public sealed class FakeResourceProbe : IResourceProbe
{
    public double CpuUsage { get; set; }

    public bool DiskBusy { get; set; }

    public bool FullScreen { get; set; }

    public Task<double> GetCpuUsageAsync(CancellationToken ct) => Task.FromResult(CpuUsage);

    public Task<bool> IsDiskBusyAsync(CancellationToken ct) => Task.FromResult(DiskBusy);

    public bool IsFullScreenExclusive() => FullScreen;
}

/// <summary>记录"进入后台 IO 作用域"的次数，用来断言 idle 路径确实降了优先级。</summary>
public sealed class RecordingBackgroundIoScope : IBackgroundIoScope
{
    public int EnterCount { get; private set; }

    public IDisposable Enter()
    {
        EnterCount++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>可切换的远端存储：模拟"拔网线/恢复网络"而不重建服务。</summary>
public sealed class SwitchingRemoteStore : GameSaveSync.Core.Sync.IRemoteStore
{
    private GameSaveSync.Core.Sync.IRemoteStore _inner;

    public SwitchingRemoteStore(GameSaveSync.Core.Sync.IRemoteStore inner) => _inner = inner;

    public GameSaveSync.Core.Sync.IRemoteStore Inner => _inner;

    public void SetInner(GameSaveSync.Core.Sync.IRemoteStore inner) => _inner = inner;

    public bool IsConfigured => _inner.IsConfigured;

    public Task PutObjectAsync(string hash, Stream content, CancellationToken ct) =>
        _inner.PutObjectAsync(hash, content, ct);

    public Task PutManifestAsync(Guid gameId, GameSaveSync.Core.Models.SnapshotManifest manifest, CancellationToken ct) =>
        _inner.PutManifestAsync(gameId, manifest, ct);

    public Task<Stream?> GetObjectAsync(string hash, CancellationToken ct) => _inner.GetObjectAsync(hash, ct);

    public Task<string?> GetManifestJsonAsync(Guid gameId, string fileName, CancellationToken ct) =>
        _inner.GetManifestJsonAsync(gameId, fileName, ct);

    public Task DeleteObjectAsync(string hash, CancellationToken ct) => _inner.DeleteObjectAsync(hash, ct);

    public Task DeleteManifestAsync(Guid gameId, string fileName, CancellationToken ct) =>
        _inner.DeleteManifestAsync(gameId, fileName, ct);

    public Task TestConnectionAsync(CancellationToken ct) => _inner.TestConnectionAsync(ct);

    public Task EnsureLayoutAsync(string deviceId, CancellationToken ct) => _inner.EnsureLayoutAsync(deviceId, ct);

    public Task<GameSaveSync.Core.Sync.RemoteIndexFetch> GetIndexAsync(string? ifNoneMatch, CancellationToken ct) =>
        _inner.GetIndexAsync(ifNoneMatch, ct);

    public Task<GameSaveSync.Core.Sync.RemoteIndexWrite> PutIndexAsync(
        string json, string? ifMatch, string? ifNoneMatch, CancellationToken ct) =>
        _inner.PutIndexAsync(json, ifMatch, ifNoneMatch, ct);

    public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory, CancellationToken ct) =>
        _inner.ListAsync(relativeDirectory, ct);

    public Task PutDeviceAsync(GameSaveSync.Core.Remote.DeviceDocument device, CancellationToken ct) =>
        _inner.PutDeviceAsync(device, ct);

    public Task<IReadOnlyList<GameSaveSync.Core.Remote.DeviceDocument>> ListDevicesAsync(CancellationToken ct) =>
        _inner.ListDevicesAsync(ct);
}
