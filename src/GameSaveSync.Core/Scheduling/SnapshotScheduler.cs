using System.Collections.Concurrent;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Detection;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Retention;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.Core.Scheduling;

public sealed class SnapshotSchedulerOptions
{
    /// <summary>游戏退出后再等多久（留给引擎 flush 写完）。规格：2s。</summary>
    public TimeSpan ExitDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>任何文件变更都重置这个定时器。规格：3s。</summary>
    public TimeSpan DebounceDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>游戏运行期间的 idle 快照间隔。规格：15min（可配 5~60）。</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromMinutes(15);

    public double IdleCpuThreshold { get; init; } = 30d;

    public bool EnablePreLaunch { get; init; } = true;

    public bool EnablePostChange { get; init; } = true;

    public bool EnableIdle { get; init; } = true;

    /// <summary>后台路径统一节流：1 MB 块 + 块间 50ms。</summary>
    public HashIoOptions IdleIoOptions { get; init; } = new() { ChunkSize = 1024 * 1024, DelayBetweenChunksMs = 50 };

    /// <summary>每次快照后自动跑一次保留策略。</summary>
    public bool RunRetentionAfterSnapshot { get; init; } = true;

    /// <summary>启动后多久做全量指纹比对（避免与 UI 初始化抢 IO）。</summary>
    public TimeSpan StartupCatchUpDelay { get; init; } = TimeSpan.FromSeconds(3);
}

public sealed record SnapshotEvent(
    Guid GameId,
    SnapshotTrigger Trigger,
    SnapshotResult Result,
    UploadOutcome? Upload);

/// <summary>
/// 触发器编排：进程事件 / 文件事件 / idle 定时 → 去抖与稳定性判定 → 快照 → 上传 → 保留策略。
/// 所有"要不要拍"的判断收敛在这里，SnapshotService 只负责"怎么拍"。
/// </summary>
public sealed class SnapshotScheduler : IAsyncDisposable
{
    private readonly IGameRepository _games;
    private readonly ISnapshotRepository _snapshots;
    private readonly ISnapshotService _snapshotService;
    private readonly IUploadService _uploadService;
    private readonly IRetentionService _retention;
    private readonly IProcessWatcher _processes;
    private readonly IFileChangeWatcher _files;
    private readonly IStabilityGate _stabilityGate;
    private readonly IResourceProbe _probe;
    private readonly IBackgroundIoScope _ioScope;
    private readonly IAppLogger _logger;
    private readonly SnapshotSchedulerOptions _options;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _debounce = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _idleTimers = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gameGates = new();
    // 全局并发度 1：SnapshotService 的 IoOptions/Skip 是实例状态，必须串行设置
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly object _pauseGate = new();

    private IReadOnlyList<Game> _gameCache = Array.Empty<Game>();
    private CancellationTokenSource? _lifetime;
    private DateTimeOffset? _pausedUntil;
    private bool _paused;
    private bool _started;

    public SnapshotScheduler(
        IGameRepository games,
        ISnapshotRepository snapshots,
        ISnapshotService snapshotService,
        IUploadService uploadService,
        IRetentionService retention,
        IProcessWatcher processes,
        IFileChangeWatcher files,
        IStabilityGate stabilityGate,
        IResourceProbe probe,
        IBackgroundIoScope ioScope,
        IAppLogger logger,
        SnapshotSchedulerOptions? options = null)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _uploadService = uploadService ?? throw new ArgumentNullException(nameof(uploadService));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _stabilityGate = stabilityGate ?? throw new ArgumentNullException(nameof(stabilityGate));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _ioScope = ioScope ?? throw new ArgumentNullException(nameof(ioScope));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SnapshotSchedulerOptions();
    }

    public SnapshotSchedulerOptions Options => _options;

    public bool IsPaused
    {
        get
        {
            lock (_pauseGate)
            {
                return _paused && (_pausedUntil is null || _pausedUntil > DateTimeOffset.UtcNow);
            }
        }
    }

    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_pauseGate)
            {
                return IsPaused ? _pausedUntil : null;
            }
        }
    }

    public bool IsGameRunning(Guid gameId) => _running.ContainsKey(gameId);

    public event Action<SnapshotEvent>? SnapshotCompleted;

    public event Action<Guid, string, Exception?>? Failed;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _lifetime = new CancellationTokenSource();
        _gameCache = await _games.ListAsync(ct).ConfigureAwait(false);

        _processes.GameStarted += OnGameStarted;
        _processes.GameExited += OnGameExited;
        _files.Changed += OnFilesChanged;

        _processes.Start(_gameCache.Where(g => g.Enabled).ToList());
        _files.Start(_gameCache.Where(g => g.Enabled).ToList());

        _logger.Info($"自动化已启用：{_gameCache.Count} 个游戏，退出延迟 {_options.ExitDelay.TotalSeconds:0.#}s，" +
                     $"去抖 {_options.DebounceDelay.TotalSeconds:0.#}s，idle 间隔 {_options.IdleInterval.TotalMinutes:0.#}min");
    }

    public Task StopAsync()
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _started = false;
        _processes.GameStarted -= OnGameStarted;
        _processes.GameExited -= OnGameExited;
        _files.Changed -= OnFilesChanged;

        _processes.Stop();
        _files.Stop();

        foreach (var cts in _debounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _debounce.Clear();

        foreach (var cts in _idleTimers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _idleTimers.Clear();

        _lifetime?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>重新读取游戏列表并刷新 watcher（添加/删除游戏后调用）。</summary>
    public async Task RefreshGamesAsync(CancellationToken ct)
    {
        _gameCache = await _games.ListAsync(ct).ConfigureAwait(false);
        var enabled = _gameCache.Where(g => g.Enabled).ToList();
        _processes.UpdateGames(enabled);
        _files.UpdateGames(enabled);
    }

    public void Pause(TimeSpan? duration)
    {
        lock (_pauseGate)
        {
            _paused = true;
            _pausedUntil = duration is null ? null : DateTimeOffset.UtcNow + duration;
        }

        _logger.Info(duration is null
            ? "已暂停自动备份（直到重启）"
            : $"已暂停自动备份 {duration.Value.TotalMinutes:0.#} 分钟");
    }

    public void Resume()
    {
        lock (_pauseGate)
        {
            _paused = false;
            _pausedUntil = null;
        }

        _logger.Info("已恢复自动备份");
    }

    /// <summary>进程启动：打 pre_launch 保险点，并启动 idle 计时。</summary>
    private void OnGameStarted(Game game, int pid)
    {
        _running[game.Id] = 1;
        _logger.Info($"检测到游戏启动：{game.Name}（PID {pid}）");

        var lifetime = _lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_options.EnablePreLaunch && !IsPaused)
                {
                    await CaptureAsync(game.Id, SnapshotTrigger.PreLaunch, "启动前保险点", background: true, lifetime)
                        .ConfigureAwait(false);
                }

                StartIdleTimer(game.Id, lifetime);
            }
            catch (Exception ex)
            {
                _logger.Warn($"启动前快照失败：{game.Name}", ex);
            }
        }, lifetime);
    }

    /// <summary>进程退出：等 ExitDelay 再拍（主力触发）。</summary>
    private void OnGameExited(Game game, int pid)
    {
        _running.TryRemove(game.Id, out _);
        StopIdleTimer(game.Id);
        _logger.Info($"检测到游戏退出：{game.Name}（PID {pid}），{_options.ExitDelay.TotalSeconds:0.#}s 后备份");

        var lifetime = _lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.ExitDelay, lifetime).ConfigureAwait(false);
                if (IsPaused)
                {
                    _logger.Info("已暂停自动备份，跳过本次 exit 快照");
                    return;
                }

                await CaptureAsync(game.Id, SnapshotTrigger.Exit, "游戏退出自动备份", background: true, lifetime)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 退出流程，忽略
            }
            catch (Exception ex)
            {
                _logger.Error($"exit 快照失败：{game.Name}", ex);
                Failed?.Invoke(game.Id, "游戏退出自动备份失败", ex);
            }
        }, lifetime);
    }

    private void OnFilesChanged(Guid gameId, bool overflow)
    {
        if (IsPaused || !_options.EnablePostChange)
        {
            return;
        }

        // 游戏运行中不靠文件事件拍快照（交给 exit / idle），避免抓到半截写入
        if (_running.ContainsKey(gameId))
        {
            return;
        }

        if (overflow)
        {
            _logger.Warn($"监视缓冲区溢出，退化为全量扫描：{gameId}");
        }

        ScheduleDebounced(gameId);
    }

    /// <summary>去抖：任何变更都重置 3s 定时器，只有"停下来"之后才进入稳定性判定。</summary>
    private void ScheduleDebounced(Guid gameId)
    {
        var lifetime = _lifetime?.Token ?? CancellationToken.None;

        var cts = new CancellationTokenSource();
        var previous = _debounce.AddOrUpdate(gameId, cts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cts;
        });

        if (!ReferenceEquals(previous, cts))
        {
            cts.Cancel();
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.DebounceDelay, cts.Token).ConfigureAwait(false);
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, lifetime);

                if (!await _stabilityGate.WaitForStableAsync(
                        await GetGameAsync(gameId, linked.Token).ConfigureAwait(false), linked.Token)
                    .ConfigureAwait(false))
                {
                    _logger.Warn($"存档目录持续变化，放弃本次 post_change 快照：{gameId}");
                    return;
                }

                if (IsPaused)
                {
                    return;
                }

                await CaptureAsync(gameId, SnapshotTrigger.PostChange, "文件变更自动备份", background: true, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 被新的变更事件重置，正常路径
            }
            catch (Exception ex)
            {
                _logger.Error($"post_change 快照失败：{gameId}", ex);
                Failed?.Invoke(gameId, "文件变更自动备份失败", ex);
            }
            finally
            {
                _debounce.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(gameId, cts));
                cts.Dispose();
            }
        }, lifetime);
    }

    private void StartIdleTimer(Guid gameId, CancellationToken lifetime)
    {
        StopIdleTimer(gameId);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _idleTimers[gameId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(_options.IdleInterval, cts.Token).ConfigureAwait(false);
                    if (!_options.EnableIdle || IsPaused || !_running.ContainsKey(gameId))
                    {
                        continue;
                    }

                    if (!await CanRunIdleAsync(cts.Token).ConfigureAwait(false))
                    {
                        _logger.Debug("idle 条件不满足（磁盘忙 / CPU 高 / 全屏独占），跳过本次");
                        continue;
                    }

                    await CaptureAsync(gameId, SnapshotTrigger.Idle, "运行中静默快照", background: true, cts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 游戏退出或应用关闭
            }
            catch (Exception ex)
            {
                _logger.Error($"idle 快照循环异常：{gameId}", ex);
            }
        }, cts.Token);
    }

    private void StopIdleTimer(Guid gameId)
    {
        if (_idleTimers.TryRemove(gameId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>idle 快照的三个前置条件必须同时满足。</summary>
    public async Task<bool> CanRunIdleAsync(CancellationToken ct)
    {
        if (_probe.IsFullScreenExclusive())
        {
            return false;
        }

        if (await _probe.IsDiskBusyAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var cpu = await _probe.GetCpuUsageAsync(ct).ConfigureAwait(false);
        return cpu < _options.IdleCpuThreshold;
    }

    /// <summary>
    /// 启动补扫：应用没运行期间的存档变更，靠一次全量比对发现。
    /// 只对"已经有至少一个快照"的游戏做，避免刚添加的游戏立刻被自动拍一张。
    /// </summary>
    public async Task<int> RunStartupCatchUpAsync(CancellationToken ct)
    {
        if (_options.StartupCatchUpDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.StartupCatchUpDelay, ct).ConfigureAwait(false);
        }

        var created = 0;
        foreach (var game in await _games.ListAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (!game.Enabled)
            {
                continue;
            }

            try
            {
                var existing = await _snapshots.CountAsync(game.Id, ct).ConfigureAwait(false);
                if (existing == 0)
                {
                    continue;
                }

                var result = await CaptureAsync(
                        game.Id, SnapshotTrigger.PostChange, "启动补扫（发现应用未运行期间的变化）",
                        background: true, ct)
                    .ConfigureAwait(false);

                if (result is { Created: true })
                {
                    created++;
                }
            }
            catch (NeedsRelocateException)
            {
                _logger.Warn($"启动补扫跳过（需重新定位）：{game.Name}");
            }
            catch (NoFilesFoundException)
            {
                // 目录空了，不产生快照
            }
            catch (Exception ex)
            {
                _logger.Error($"启动补扫失败：{game.Name}", ex);
            }
        }

        if (created > 0)
        {
            _logger.Info($"启动补扫完成：补出 {created} 个快照");
        }

        return created;
    }

    /// <summary>手动/自动统一的快照入口（带并发保护、上传与保留策略）。</summary>
    public async Task<SnapshotResult?> CaptureAsync(
        Guid gameId,
        SnapshotTrigger trigger,
        string? note,
        bool background,
        CancellationToken ct)
    {
        var gate = _gameGates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.Debug($"该游戏已有快照在进行，跳过本次 {trigger.ToDisplayName()}");
            return null;
        }

        // 全局串行：规格要求后台并发度 1，同时保证 IoOptions 这类实例状态不被并发改写
        var acquiredGlobal = false;
        try
        {
            await _captureGate.WaitAsync(ct).ConfigureAwait(false);
            acquiredGlobal = true;

            var service = _snapshotService as SnapshotService;
            var previousIo = service?.IoOptions;
            var previousSkip = service?.SkipIfUnchangedWithParent;

            if (service is not null)
            {
                service.IoOptions = background ? _options.IdleIoOptions : Abstractions.HashIoOptions.Default;
                service.SkipIfUnchangedWithParent = trigger != SnapshotTrigger.Manual;
            }

            using var scope = background ? _ioScope.Enter() : null;
            try
            {
                var result = await _snapshotService.CreateAsync(gameId, trigger, note, ct).ConfigureAwait(false);

                UploadOutcome? upload = null;
                if (result.Created && result.SnapshotId is { } snapshotId)
                {
                    try
                    {
                        upload = await _uploadService.UploadSnapshotAsync(snapshotId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error("自动上传失败（快照已落盘）", ex);
                    }

                    if (_options.RunRetentionAfterSnapshot)
                    {
                        try
                        {
                            await _retention.RunAsync(gameId, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.Warn("保留策略执行失败", ex);
                        }
                    }
                }

                SnapshotCompleted?.Invoke(new SnapshotEvent(gameId, trigger, result, upload));
                return result;
            }
            finally
            {
                if (service is not null)
                {
                    service.IoOptions = previousIo ?? Abstractions.HashIoOptions.Default;
                    service.SkipIfUnchangedWithParent = previousSkip ?? false;
                }
            }
        }
        finally
        {
            if (acquiredGlobal)
            {
                _captureGate.Release();
            }

            gate.Release();
        }
    }

    private async Task<Game> GetGameAsync(Guid gameId, CancellationToken ct) =>
        await _games.GetAsync(gameId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"游戏不存在：{gameId}");

    public ValueTask DisposeAsync()
    {
        _processes.Dispose();
        _files.Dispose();
        return new ValueTask(StopAsync());
    }
}
