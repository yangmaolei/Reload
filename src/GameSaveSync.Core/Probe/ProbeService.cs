using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.Core.Probe;

/// <summary>探测参数。默认覆盖用户目录、AppData、Documents、Saved Games，深度受限。</summary>
public sealed class ProbeOptions
{
    /// <summary>单次记录的指纹条数上限，防止把整块盘读进内存。</summary>
    public int MaxFiles { get; init; } = 120_000;

    public int MaxDepth { get; init; } = 6;

    /// <summary>超过这个大小的文件只记名字不记指纹（存档不会是几百 MB）。</summary>
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024;
}

public sealed record ProbeRoots(IReadOnlyList<string> Roots);

public sealed record ProbeStart(Guid Id, long StartedAt, int FileCount, IReadOnlyList<string> Roots, TimeSpan Elapsed);

public sealed record ProbeFileSample(string Path, long Size);

/// <summary>一个候选目录：为什么可疑、变了多少、示例文件。</summary>
public sealed record ProbeCandidate(
    string Directory,
    int NewFiles,
    int ModifiedFiles,
    long AddedBytes,
    double Score,
    IReadOnlyList<ProbeFileSample> Samples,
    string Reason);

public sealed record ProbeResult(
    IReadOnlyList<ProbeCandidate> Candidates,
    int ScannedFiles,
    int ChangedFiles,
    TimeSpan Elapsed);

/// <summary>
/// 探测模式：记录一批候选目录的指纹 → 用户去玩游戏 → 回来对比。
/// 只做"目录级"打分，绝不自动写入游戏配置（让用户勾选确认）。
/// </summary>
public sealed class ProbeService
{
    private static readonly string[] SaveExtensions =
    {
        ".sav", ".save", ".savx", ".slot", ".srm", ".sgm", ".rpgsave", ".rxdata", ".rvdata2",
        ".lsd", ".es3", ".profile", ".user", ".sga", ".ugc", ".ss1", ".gam", ".dat",
    };

    private static readonly string[] SaveHints =
    {
        "save", "saves", "savedata", "savegame", "savegames", "profile", "slot", "userdata", "persistent",
    };

    private static readonly string[] SkipDirectoryNames =
    {
        "windows", "$recycle.bin", "system volume information", "node_modules", ".git", ".vs", "obj", "bin",
        "temp", "tmp", "cache", "caches", "crashdumps", "crashpad", "gpucache", "code cache", "service worker",
        "logs", "log", "shadercache", "installer", "redist", "_commonredist", "diagnostics",
    };

    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly Dictionary<Guid, Session> _sessions = new();

    public ProbeService(IFileSystem fileSystem, IClock clock, IAppLogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>把"最可能放存档的地方"拼成候选根，额外加上游戏安装目录。</summary>
    public static ProbeRoots BuildDefaultRoots(
        string userProfile,
        string appData,
        string localAppData,
        string documents,
        string savedGames,
        string? installDir)
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                roots.Add(path);
            }
        }

        Add(Path.Combine(userProfile, "Saved Games"));
        Add(documents);
        Add(Path.Combine(appData));
        Add(Path.Combine(localAppData));
        Add(Path.Combine(userProfile, "AppData", "LocalLow"));
        Add(installDir);

        return new ProbeRoots(roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<ProbeStart> StartAsync(ProbeRoots roots, ProbeOptions? options, CancellationToken ct) =>
        Task.Run(() => StartCore(roots, options ?? new ProbeOptions(), ct), ct);

    private ProbeStart StartCore(ProbeRoots roots, ProbeOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var files = new Dictionary<string, ProbeEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Roots)
        {
            Scan(root, root, 0, options, files, ct);
        }

        var id = Guid.NewGuid();
        _sessions[id] = new Session(roots.Roots, files, _clock.UtcNowMs, DateTimeOffset.UtcNow);
        stopwatch.Stop();

        _logger.Info($"探测开始：{files.Count} 个文件，{roots.Roots.Count} 个根，耗时 {stopwatch.ElapsedMilliseconds} ms");
        return new ProbeStart(id, _clock.UtcNowMs, files.Count, roots.Roots, stopwatch.Elapsed);
    }

    public Task<ProbeResult> CompleteAsync(ProbeStart start, ProbeOptions? options, CancellationToken ct) =>
        Task.Run(() => CompleteCore(start, options ?? new ProbeOptions(), ct), ct);

    private ProbeResult CompleteCore(ProbeStart start, ProbeOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!_sessions.TryGetValue(start.Id, out var session))
        {
            throw new InvalidOperationException("探测会话不存在或已过期，请重新开始探测");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var after = new Dictionary<string, ProbeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in session.Roots)
        {
            Scan(root, root, 0, options, after, ct);
        }

        var changed = new List<(string Path, bool IsNew, long Size, long Delta)>();
        foreach (var (path, entry) in after)
        {
            ct.ThrowIfCancellationRequested();

            if (!session.Before.TryGetValue(path, out var before))
            {
                changed.Add((path, true, entry.Size, entry.Size));
            }
            else if (before.Size != entry.Size || before.MtimeMs != entry.MtimeMs)
            {
                changed.Add((path, false, entry.Size, entry.Size - before.Size));
            }
        }

        // 探测期间被删除的文件也要报告：游戏回滚存档/清理临时文件是常见场景，
        // 只看"现存文件"会完全漏掉这类变化。
        foreach (var (path, before) in session.Before)
        {
            ct.ThrowIfCancellationRequested();

            if (!after.ContainsKey(path))
            {
                changed.Add((path, false, 0, -before.Size));
            }
        }

        var candidates = BuildCandidates(changed, session.Roots);
        stopwatch.Stop();
        _sessions.Remove(start.Id);

        _logger.Info($"探测完成：{changed.Count} 个文件变化，{candidates.Count} 个候选目录，耗时 {stopwatch.ElapsedMilliseconds} ms");
        return new ProbeResult(candidates, after.Count, changed.Count, stopwatch.Elapsed);
    }

    private void Scan(
        string root,
        string directory,
        int depth,
        ProbeOptions options,
        Dictionary<string, ProbeEntry> sink,
        CancellationToken ct)
    {
        if (depth > options.MaxDepth || sink.Count >= options.MaxFiles)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        IReadOnlyList<string> files;
        IReadOnlyList<string> subDirs;
        try
        {
            files = _fileSystem.EnumerateFiles(directory, recursive: false);
            subDirs = _fileSystem.EnumerateDirectories(directory, recursive: false);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            if (sink.Count >= options.MaxFiles)
            {
                return;
            }

            try
            {
                var stat = _fileSystem.Stat(file);
                if (stat.Size > options.MaxFileBytes)
                {
                    continue;
                }

                sink[file] = new ProbeEntry(stat.Size, stat.MtimeMs);
            }
            catch
            {
                // 单个文件读不到就算了，不能让整次探测失败
            }
        }

        foreach (var child in subDirs)
        {
            var name = Path.GetFileName(child).ToLowerInvariant();
            if (SkipDirectoryNames.Contains(name))
            {
                continue;
            }

            // 重解析点（junction/symlink）不跟，避免绕回系统目录
            try
            {
                var attributes = _fileSystem.Stat(child).Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            Scan(root, child, depth + 1, options, sink, ct);
        }
    }

    private static List<ProbeCandidate> BuildCandidates(
        List<(string Path, bool IsNew, long Size, long Delta)> changed,
        IReadOnlyList<string> roots)
    {
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, isNew, size, delta) in changed)
        {
            var directory = Path.GetDirectoryName(path) ?? path;
            var collapsed = CollapseToSaveRoot(directory);
            if (!groups.TryGetValue(collapsed, out var group))
            {
                group = new Group(collapsed);
                groups[collapsed] = group;
            }

            group.Add(path, isNew, size, delta);
        }

        var candidates = new List<ProbeCandidate>();

        foreach (var group in groups.Values)
        {
            var lower = group.Directory.ToLowerInvariant();
            var saveLike = SaveHints.Any(h => lower.Contains(h, StringComparison.Ordinal));

            // 明显是程序/补丁目录：新增了 exe/dll 而不是存档
            if (group.ExtensionHits.TryGetValue(".exe", out var exeCount) && exeCount > 0)
            {
                continue;
            }

            if (group.ExtensionHits.TryGetValue(".dll", out var dllCount) && dllCount >= 2 && group.NewFiles >= 2)
            {
                continue;
            }

            var saveExtensionHits = group.ExtensionHits
                .Where(kv => SaveExtensions.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);

            if (!saveLike && saveExtensionHits == 0 && group.NewFiles + group.ModifiedFiles < 3)
            {
                continue;
            }

            var score = group.NewFiles * 3.0 + group.ModifiedFiles * 1.0;
            score += Math.Min(saveExtensionHits, 10) * 2.0;
            score += Math.Min(group.ExtensionHits.Count, 4) * 0.5;
            if (saveLike)
            {
                score += 6;
            }

            // 一个目录里变了几百个文件，多半是更新/缓存，不是存的进度
            if (group.NewFiles + group.ModifiedFiles > 2000)
            {
                score -= 30;
            }

            var reason = BuildReason(group, saveLike, saveExtensionHits);
            candidates.Add(new ProbeCandidate(
                group.Directory,
                group.NewFiles,
                group.ModifiedFiles,
                group.AddedBytes,
                Math.Round(score, 1),
                group.Samples.Take(5).ToList(),
                reason));
        }

        return candidates
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.NewFiles)
            .ThenBy(c => c.Directory, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static string BuildReason(Group group, bool saveLike, int saveHits)
    {
        var parts = new List<string>();
        if (group.NewFiles > 0)
        {
            parts.Add($"新增 {group.NewFiles} 个文件");
        }

        if (group.ModifiedFiles > 0)
        {
            parts.Add($"修改 {group.ModifiedFiles} 个");
        }

        if (saveHits > 0)
        {
            parts.Add($"其中 {saveHits} 个是存档扩展名");
        }

        if (saveLike)
        {
            parts.Add("路径含 save/profile");
        }

        return string.Join("，", parts);
    }

    /// <summary>把 "…\Saves\slot1" 收起为 "…\Saves"，避免候选碎成一堆槽位目录。</summary>
    private static string CollapseToSaveRoot(string directory)
    {
        var current = directory;
        for (var i = 0; i < 3; i++)
        {
            var name = Path.GetFileName(current).ToLowerInvariant();
            if (SaveHints.Any(h => name.Contains(h, StringComparison.Ordinal)))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length < current.Length)
            {
                break;
            }

            current = parent;
        }

        return directory;
    }

    private sealed record ProbeEntry(long Size, long MtimeMs);

    private sealed class Session
    {
        public Session(IReadOnlyList<string> roots, Dictionary<string, ProbeEntry> before, long startedAt, DateTimeOffset createdUtc)
        {
            Roots = roots;
            Before = before;
            StartedAt = startedAt;
            CreatedUtc = createdUtc;
        }

        public IReadOnlyList<string> Roots { get; }

        public Dictionary<string, ProbeEntry> Before { get; }

        public long StartedAt { get; }

        public DateTimeOffset CreatedUtc { get; }
    }

    private sealed class Group
    {
        public Group(string directory) => Directory = directory;

        public string Directory { get; }

        public int NewFiles { get; private set; }

        public int ModifiedFiles { get; private set; }

        public long AddedBytes { get; private set; }

        public Dictionary<string, int> ExtensionHits { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ProbeFileSample> Samples { get; } = new();

        public void Add(string path, bool isNew, long size, long delta)
        {
            if (isNew)
            {
                NewFiles++;
            }
            else
            {
                ModifiedFiles++;
            }

            AddedBytes += Math.Max(delta, 0);

            var extension = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(extension))
            {
                ExtensionHits[extension] = ExtensionHits.TryGetValue(extension, out var count) ? count + 1 : 1;
            }

            if (Samples.Count < 5)
            {
                Samples.Add(new ProbeFileSample(path, size));
            }
        }
    }
}
