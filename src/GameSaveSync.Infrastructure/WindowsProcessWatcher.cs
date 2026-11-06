using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Scheduling;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// Windows 进程监控。设计取舍：
/// <list type="bullet">
/// <item>WMI <c>Win32_ProcessStartTrace/StopTrace</c> 只当**加速器**：收到事件立刻触发一次轮询。</item>
/// <item>2 秒轮询是**真相来源**（WMI 偶发丢事件，且非管理员身份常订阅失败）。</item>
/// <item>匹配规则在 <see cref="GameProcessMatcher"/>：exe 名 + 必须位于安装目录下。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessWatcher : IProcessWatcher
{
    private readonly IAppLogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly ConcurrentDictionary<int, Guid> _tracked = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly List<ManagementEventWatcher> _wmiWatchers = new();

    private volatile List<Game> _games = new();
    private CancellationTokenSource? _cts;
    private Task? _pollLoop;
    private bool _disposed;

    public WindowsProcessWatcher(IAppLogger logger, TimeSpan? pollInterval = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public event Action<Game, int>? GameStarted;

    public event Action<Game, int>? GameExited;

    public void Start(IReadOnlyList<Game> games)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _games = games.ToList();
        _cts = new CancellationTokenSource();

        TrySubscribeWmi();
        _pollLoop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void UpdateGames(IReadOnlyList<Game> games) => _games = games.ToList();

    public void Stop()
    {
        _cts?.Cancel();
        foreach (var watcher in _wmiWatchers)
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch
            {
                // 关闭路径忽略
            }
        }

        _wmiWatchers.Clear();
        _tracked.Clear();
    }

    /// <summary>订阅 WMI 只为了"更快知道"，失败不致命。</summary>
    private void TrySubscribeWmi()
    {
        try
        {
            foreach (var query in new[]
                     {
                         "SELECT * FROM Win32_ProcessStartTrace",
                         "SELECT * FROM Win32_ProcessStopTrace",
                     })
            {
                var watcher = new ManagementEventWatcher(new WqlEventQuery(query));
                watcher.EventArrived += (_, _) => TriggerImmediatePoll();
                watcher.Start();
                _wmiWatchers.Add(watcher);
            }

            _logger.Debug("WMI 进程事件订阅成功（仅作为加速器）");
        }
        catch (Exception ex)
        {
            // 非管理员身份/系统策略常常禁止 WMI trace：直接退化到纯轮询。
            // 这是预期降级路径（每次启动都会遇到），不需要把异常栈刷进日志。
            _logger.Warn("WMI 进程事件订阅不可用，改用 2 秒轮询（功能不受影响）");
            _logger.Debug($"WMI 订阅失败详情：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TriggerImmediatePoll()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        if (!token.IsCancellationRequested)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await PollOnceAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // 轮询异常交给下一轮
                }
            }, token);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("进程轮询异常（继续）", ex);
            }

            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var games = _games;
            if (games.Count == 0 || games.All(g => g.ExeNames.Count == 0))
            {
                return;
            }

            var matched = new Dictionary<int, Guid>();
            foreach (var process in EnumerateCandidateProcesses(games))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var game in games)
                {
                    if (GameProcessMatcher.Matches(game, process))
                    {
                        matched[process.Pid] = game.Id;
                        break;
                    }
                }
            }

            // 新出现的
            foreach (var (pid, gameId) in matched)
            {
                if (_tracked.ContainsKey(pid))
                {
                    continue;
                }

                _tracked[pid] = gameId;
                var game = games.FirstOrDefault(g => g.Id == gameId);
                if (game is not null)
                {
                    _logger.Info($"进程匹配到游戏：{game.Name}（PID {pid}）");
                    GameStarted?.Invoke(game, pid);
                }
            }

            // 消失的
            foreach (var pid in _tracked.Keys.ToList())
            {
                if (matched.ContainsKey(pid))
                {
                    continue;
                }

                if (_tracked.TryRemove(pid, out var gameId))
                {
                    var game = games.FirstOrDefault(g => g.Id == gameId);
                    if (game is not null)
                    {
                        GameExited?.Invoke(game, pid);
                    }
                }
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>只对"名字命中配置"的进程去取路径（避免对全系统进程做 MainModule 访问）。</summary>
    private static IReadOnlyList<GameProcessMatcher.RunningProcess> EnumerateCandidateProcesses(IReadOnlyList<Game> games)
    {
        var names = new HashSet<string>(
            games.SelectMany(g => g.ExeNames)
                .Select(n =>
                {
                    var file = Path.GetFileName(n.Trim());
                    return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
                })
                .Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<GameProcessMatcher.RunningProcess>();
        if (names.Count == 0)
        {
            return result;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var process in processes)
        {
            var matchedName = string.Empty;
            var pid = 0;
            string? path = null;

            try
            {
                matchedName = process.ProcessName + ".exe";
                pid = process.Id;

                if (!names.Contains(matchedName))
                {
                    continue;
                }

                path = process.MainModule?.FileName;
            }
            catch
            {
                // 受保护进程读不到路径：退化为只用 exe 名匹配
                path = null;
            }
            finally
            {
                process.Dispose();
            }

            if (pid != 0 && names.Contains(matchedName))
            {
                result.Add(new GameProcessMatcher.RunningProcess(pid, matchedName, path));
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts?.Dispose();
        _pollGate.Dispose();
        _pollLoop?.Dispose();
    }
}
