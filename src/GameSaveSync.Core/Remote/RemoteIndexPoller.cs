using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.Core.Remote;

/// <summary>
/// 下行轮询器：只读 <c>index.json</c> 一个文件，默认 60 秒一次，带 If-None-Match。
/// 绝大部分轮询会命中 304（几乎零成本），发现未知 snapshotId 才去拉清单与对象。
/// </summary>
public sealed class RemoteIndexPoller : IAsyncDisposable
{
    /// <summary>规格要求的默认轮询间隔（60 秒）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    private readonly IRemoteIndexService _index;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _cts = new();

    public RemoteIndexPoller(IRemoteIndexService index, IAppLogger logger, TimeSpan? interval = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Interval = interval ?? DefaultInterval;
    }

    public TimeSpan Interval { get; }

    public int PollCount { get; private set; }

    public int NotModifiedCount { get; private set; }

    public event Action<IndexPullResult>? Changed;

    /// <summary>单次轮询（测试与"立即检查"按钮都走这里）。</summary>
    public async Task<IndexPullResult> PollOnceAsync(CancellationToken ct)
    {
        PollCount++;
        var result = await _index.PullAsync(ct).ConfigureAwait(false);
        if (result.Changed)
        {
            Changed?.Invoke(result);
        }
        else if (result.Error is null)
        {
            NotModifiedCount++;
        }
        else if (!result.Error.Contains("尚未配置", StringComparison.Ordinal))
        {
            // 未配置远端是正常状态，不必每分钟记一条日志
            _logger.Debug($"轮询 index.json 失败：{result.Error}");
        }

        return result;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        _logger.Info($"下行轮询已启动：每 {Interval.TotalSeconds:0} 秒检查一次 index.json");

        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("轮询循环异常（继续）", ex);
            }

            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
