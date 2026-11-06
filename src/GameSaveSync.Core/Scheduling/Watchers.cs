using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Scheduling;

/// <summary>进程事件来源（实现：Infrastructure.WindowsProcessWatcher，WMI + 轮询兜底）。</summary>
public interface IProcessWatcher : IDisposable
{
    event Action<Game, int>? GameStarted;

    event Action<Game, int>? GameExited;

    void Start(IReadOnlyList<Game> games);

    void UpdateGames(IReadOnlyList<Game> games);

    void Stop();
}

/// <summary>存档目录变更事件来源（实现：Infrastructure.SaveFolderWatcher，64KB 缓冲）。</summary>
public interface IFileChangeWatcher : IDisposable
{
    /// <summary>overflow=true 表示缓冲区溢出，必须退化为全量扫描。</summary>
    event Action<Guid, bool>? Changed;

    void Start(IReadOnlyList<Game> games);

    void UpdateGames(IReadOnlyList<Game> games);

    void Stop();
}

/// <summary>
/// 资源探针：idle 快照的前置条件（磁盘空闲、CPU &lt; 30%、非全屏独占）。
/// 任一条件不满足就不做 idle 快照。
/// </summary>
public interface IResourceProbe
{
    Task<double> GetCpuUsageAsync(CancellationToken ct);

    Task<bool> IsDiskBusyAsync(CancellationToken ct);

    bool IsFullScreenExclusive();
}

/// <summary>后台 IO 作用域：进入后线程优先级降为 IDLE，退出时恢复。</summary>
public interface IBackgroundIoScope
{
    IDisposable Enter();
}

/// <summary>进程匹配（纯函数，便于单测）。</summary>
public static class GameProcessMatcher
{
    public sealed record RunningProcess(int Pid, string Name, string? Path);

    /// <summary>
    /// 规则：exe 名（不区分大小写）匹配 + 若配置了安装目录，则进程路径必须位于该目录下。
    /// 目的是避免误判启动器 / 反作弊 / 同名 exe。
    /// </summary>
    public static bool Matches(Game game, RunningProcess process)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(process);

        if (game.ExeNames.Count == 0)
        {
            return false;
        }

        var exeName = Path.GetFileName(process.Name);
        var matchedName = game.ExeNames.Any(name =>
        {
            var candidate = Path.GetFileName(name.Trim());
            var normalized = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? candidate
                : candidate + ".exe";
            return string.Equals(normalized, exeName, StringComparison.OrdinalIgnoreCase);
        });

        if (!matchedName)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(game.InstallDir) || string.IsNullOrWhiteSpace(process.Path))
        {
            // 没配安装目录时只靠 exe 名匹配（用户自行承担误判风险）
            return true;
        }

        var installDir = game.InstallDir.TrimEnd('\\', '/');
        return process.Path.StartsWith(installDir + "\\", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(process.Path, installDir, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<Guid> MatchGames(IReadOnlyList<Game> games, IReadOnlyList<RunningProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(processes);

        var matched = new List<Guid>();
        foreach (var game in games)
        {
            if (game.Enabled && processes.Any(p => Matches(game, p)))
            {
                matched.Add(game.Id);
            }
        }

        return matched;
    }
}
