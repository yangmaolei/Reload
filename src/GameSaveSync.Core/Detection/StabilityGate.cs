using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;

namespace GameSaveSync.Core.Detection;

public interface IStabilityGate
{
    /// <summary>
    /// 目录级稳定性判定：所有候选文件 stat 一遍 → 等 <see cref="StabilityOptions.RecheckDelay"/> → 再 stat。
    /// 全部 size/mtime 未变才算稳定；否则按 200/400/800/1600/3200ms 退避重试。
    /// </summary>
    Task<bool> WaitForStableAsync(Game game, CancellationToken ct);
}

public sealed class StabilityOptions
{
    /// <summary>算完之后的静默复检间隔（规格：400ms）。</summary>
    public TimeSpan RecheckDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>不稳定时的退避序列（规格：200/400/800/1600/3200，最多 5 次）。</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = new[]
    {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1600),
        TimeSpan.FromMilliseconds(3200),
    };
}

/// <summary>
/// 快照前的"门"：只有整棵存档目录静下来才允许开快照。
/// 注意与 SnapshotService 的单文件稳定性校验是两层保护，这里挡的是"多文件写入尚未结束"。
/// </summary>
public sealed class StabilityGate : IStabilityGate
{
    private readonly IFileSystem _fileSystem;
    private readonly IPathTokenResolver _tokens;
    private readonly StabilityOptions _options;

    public StabilityGate(
        IFileSystem fileSystem,
        IPathTokenResolver tokens,
        StabilityOptions? options = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _options = options ?? new StabilityOptions();
    }

    public async Task<bool> WaitForStableAsync(Game game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (await IsStableOnceAsync(game, ct).ConfigureAwait(false))
        {
            return true;
        }

        foreach (var delay in _options.RetryDelays)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(delay, ct).ConfigureAwait(false);

            if (await IsStableOnceAsync(game, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsStableOnceAsync(Game game, CancellationToken ct)
    {
        var before = TakeSnapshot(game);
        if (before.Count == 0)
        {
            // 没有文件可看：交给上层判定（空目录不会产生快照）
            return true;
        }

        await Task.Delay(_options.RecheckDelay, ct).ConfigureAwait(false);

        var after = TakeSnapshot(game);
        if (before.Count != after.Count)
        {
            return false;
        }

        foreach (var (path, entry) in before)
        {
            if (!after.TryGetValue(path, out var current) || !entry.MetadataEquals(current))
            {
                return false;
            }
        }

        return true;
    }

    private Dictionary<string, FileEntry> TakeSnapshot(Game game)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);

        foreach (var root in resolution.Roots)
        {
            if (root.AbsolutePath is null || !root.DirectoryExists)
            {
                continue;
            }

            IReadOnlyList<string> files;
            try
            {
                files = _fileSystem.EnumerateFiles(root.AbsolutePath, root.Root.Recursive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    result[file] = _fileSystem.Stat(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 正被写入/删除的文件：视为不稳定，交给重试
                    result[file] = new FileEntry(-1, -1, default);
                }
            }
        }

        return result;
    }
}
