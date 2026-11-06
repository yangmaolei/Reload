using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.Core.Sync;

public sealed class OutboxOptions
{
    /// <summary>连续失败多少次后标记 failed 并通知用户（规格：20）。</summary>
    public int MaxAttempts { get; init; } = 20;

    /// <summary>后台循环的检查间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>单次 ProcessDue 最多处理多少项，避免长时间占用。</summary>
    public int BatchLimit { get; init; } = 200;
}

/// <summary>
/// outbox 处理器。两条入口：
/// 1) <see cref="ProcessDueAsync"/> —— 手动"重试上传"或测试直接驱动；
/// 2) <see cref="RunAsync"/> —— 常驻后台循环，平时每 2 秒醒一次。
/// 语义：同一时刻只有一个处理器在跑（内部信号量），失败按指数退避排下一次。
/// </summary>
public sealed class OutboxProcessor : IAsyncDisposable
{
    private readonly IOutboxStore _store;
    private readonly IRemoteOperationExecutor _executor;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly OutboxOptions _options;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _running;

    public OutboxProcessor(
        IOutboxStore store,
        IRemoteOperationExecutor executor,
        IClock clock,
        IAppLogger logger,
        OutboxOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new OutboxOptions();
    }

    public OutboxOptions Options => _options;

    /// <summary>某项连续失败达到上限（默认 20 次），UI 应提示用户。</summary>
    public event Action<OutboxFailure>? PermanentlyFailed;

    /// <summary>唤醒后台循环立即处理（手动重试上传）。</summary>
    public void Kick()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // 已有信号，忽略
        }
    }

    public async Task<OutboxRunResult> ProcessDueAsync(CancellationToken ct)
    {
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        var processed = 0;
        var succeeded = 0;
        var failed = 0;
        var permanentlyFailed = 0;

        try
        {
            while (processed < _options.BatchLimit)
            {
                ct.ThrowIfCancellationRequested();

                var item = await _store.LeaseDueAsync(_clock.UtcNowMs, ct).ConfigureAwait(false);
                if (item is null)
                {
                    break;
                }

                processed++;
                try
                {
                    await _executor.ExecuteAsync(item, ct).ConfigureAwait(false);
                    await _store.CompleteAsync(item.Id, ct).ConfigureAwait(false);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    // 取消时把项放回 pending，保证下次还能重试。
                    // 这里必须用 CancellationToken.None：ct 此刻已取消，
                    // 拿它回写会立刻抛异常，FailAsync 根本没执行 → 项永远卡在 inflight。
                    await _store.FailAsync(item.Id, "已取消", 0, permanent: false, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    var permanent = item.Attempts >= _options.MaxAttempts;
                    if (permanent)
                    {
                        permanentlyFailed++;
                    }

                    var delay = OutboxBackoff.Compute(item.Attempts);
                    var next = _clock.UtcNowMs + (long)delay.TotalMilliseconds;
                    await _store.FailAsync(item.Id, ex.Message, next, permanent, ct).ConfigureAwait(false);

                    if (permanent)
                    {
                        _logger.Error(
                            $"outbox 项连续失败 {item.Attempts} 次，已标记 failed：{item.Op} / {item.DedupeKey}", ex);
                        PermanentlyFailed?.Invoke(new OutboxFailure(item, ex.Message));
                    }
                    else
                    {
                        _logger.Warn(
                            $"outbox 项失败（第 {item.Attempts} 次，{delay.TotalSeconds:0.#}s 后重试）：" +
                            $"{item.Op} / {item.DedupeKey} — {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            _runGate.Release();
        }

        return new OutboxRunResult(processed, succeeded, failed, permanentlyFailed);
    }

    /// <summary>常驻后台循环；进程退出前 cancel 即可。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _running = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _running.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(token).ConfigureAwait(false);
                await _signal.WaitAsync(_options.PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("outbox 后台循环异常（继续）", ex);
                try
                {
                    await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _running?.Cancel();
        _running?.Dispose();
        _runGate.Dispose();
        _signal.Dispose();
        return ValueTask.CompletedTask;
    }
}
