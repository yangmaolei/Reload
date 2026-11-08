using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Export;

/// <summary>单个游戏的导出结果。</summary>
public sealed record BatchExportItem(
    Guid GameId,
    string GameName,
    bool Success,
    int FileCount,
    long TotalBytes,
    string? Error)
{
    public string Summary => Success
        ? $"{GameName}：{FileCount} 个文件，{RestorePlan.FormatBytes(TotalBytes)}"
        : $"{GameName}：失败 —— {Error}";
}

/// <summary>一次批量导出的整体结果。</summary>
public sealed record BatchExportResult(
    string OutputPath,
    IReadOnlyList<BatchExportItem> Items)
{
    public int SuccessCount => Items.Count(i => i.Success);

    public int FailureCount => Items.Count(i => !i.Success);

    public int TotalFileCount => Items.Sum(i => i.FileCount);

    public long TotalBytes => Items.Sum(i => i.TotalBytes);

    public bool AllSucceeded => FailureCount == 0;

    /// <summary>空结果（没有任何游戏被选中）。</summary>
    public static BatchExportResult Nothing { get; } =
        new(string.Empty, Array.Empty<BatchExportItem>());

    /// <summary>给用户看的一行摘要。</summary>
    public string Summary => Items.Count == 0
        ? "没有可导出的游戏"
        : $"成功 {SuccessCount}/{Items.Count}" +
          (FailureCount > 0 ? $"，失败 {FailureCount}" : string.Empty) +
          $"，共 {TotalFileCount} 个文件、{RestorePlan.FormatBytes(TotalBytes)}";
}

/// <summary>
/// 批量导出存档：把若干游戏的存档目录原样复制到一个统一目录，或打包成一个 zip。
/// <para>
/// "原样复制"指保留文件内容与相对路径结构，不做任何转换 —— 用户拿到导出目录就能
/// 直接放回游戏的存档位置。这是与"快照"（内容寻址、用于跨机同步）完全不同的用途：
/// 快照面向同步，导出面向交接与归档。
/// </para>
/// </summary>
public interface IBatchSaveExporter
{
    /// <summary>
    /// 把每个游戏的存档导出到 <paramref name="destinationRoot"/> 下的独立子目录。
    /// 子目录名用 "游戏名_游戏ID前8位"，同名游戏不会互相覆盖。
    /// </summary>
    Task<BatchExportResult> ExportToDirectoryAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationRoot,
        bool overwriteExisting,
        CancellationToken ct);

    /// <summary>
    /// 把每个游戏的存档打包进同一个 zip，压缩包内按 "游戏名/..." 分目录。
    /// </summary>
    Task<BatchExportResult> ExportToZipAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationZipPath,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class BatchSaveExporter : IBatchSaveExporter
{
    private readonly IGameRepository _games;
    private readonly IFileSystem _fileSystem;
    private readonly IPathTokenResolver _tokens;
    private readonly IAppLogger _logger;

    public BatchSaveExporter(
        IGameRepository games,
        IFileSystem fileSystem,
        IPathTokenResolver tokens,
        IAppLogger logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BatchExportResult> ExportToDirectoryAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationRoot,
        bool overwriteExisting,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(gameIds);

        var root = _fileSystem.GetFullPath(destinationRoot);
        _fileSystem.CreateDirectory(root);

        var items = new List<BatchExportItem>(gameIds.Count);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gameId in gameIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            var game = await _games.GetAsync(gameId, ct).ConfigureAwait(false);
            if (game is null)
            {
                items.Add(new BatchExportItem(gameId, "(已删除的游戏)", false, 0, 0, "游戏记录不存在"));
                continue;
            }

            var folderName = MakeFolderName(game, usedFolders);
            var target = _fileSystem.Combine(root, folderName);

            try
            {
                // 目标已存在：按要求区分"覆盖"与"跳过"，绝不能静默混进旧内容
                if (_fileSystem.DirectoryExists(target))
                {
                    if (!overwriteExisting)
                    {
                        items.Add(new BatchExportItem(
                            game.Id, game.Name, false, 0, 0,
                            $"目标目录已存在：{folderName}（可选择覆盖）"));
                        continue;
                    }

                    _fileSystem.DeleteDirectory(target, recursive: true);
                }

                var copied = await CopyGameFilesAsync(game, target, ct).ConfigureAwait(false);

                // 一个可解析的存档根都没有 → 这个游戏没法导出，明确报失败，
                // 不能报成"成功但 0 个文件"，那会让人以为存档是空的
                if (!copied.HasResolvableRoot)
                {
                    items.Add(new BatchExportItem(
                        game.Id, game.Name, false, 0, 0, "本机找不到该游戏的存档目录（可能需要重新定位）"));
                    continue;
                }

                items.Add(new BatchExportItem(game.Id, game.Name, true, copied.FileCount, copied.TotalBytes, null));
            }
            catch (Exception ex)
            {
                _logger.Error($"批量导出失败：{game.Name}", ex);
                items.Add(new BatchExportItem(game.Id, game.Name, false, 0, 0, ex.Message));
            }
        }

        var result = new BatchExportResult(root, items);
        _logger.Info($"批量导出到目录完成：{result.Summary} → {root}");
        return result;
    }

    public async Task<BatchExportResult> ExportToZipAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationZipPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        ArgumentNullException.ThrowIfNull(gameIds);

        var full = _fileSystem.GetFullPath(destinationZipPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }

        // 已存在直接覆盖：导出是"重新生成一份"的语义，留着旧文件反而会误导
        if (_fileSystem.FileExists(full))
        {
            _fileSystem.DeleteFile(full);
        }

        var items = new List<BatchExportItem>(gameIds.Count);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var archive = System.IO.Compression.ZipFile.Open(full, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var gameId in gameIds.Distinct())
            {
                ct.ThrowIfCancellationRequested();

                var game = await _games.GetAsync(gameId, ct).ConfigureAwait(false);
                if (game is null)
                {
                    items.Add(new BatchExportItem(gameId, "(已删除的游戏)", false, 0, 0, "游戏记录不存在"));
                    continue;
                }

                var folderName = MakeFolderName(game, usedFolders);
                try
                {
                    var written = await AddGameFilesToArchiveAsync(game, archive, folderName, ct).ConfigureAwait(false);

                    if (!written.HasResolvableRoot)
                    {
                        items.Add(new BatchExportItem(
                            game.Id, game.Name, false, 0, 0, "本机找不到该游戏的存档目录（可能需要重新定位）"));
                        continue;
                    }

                    items.Add(new BatchExportItem(game.Id, game.Name, true, written.FileCount, written.TotalBytes, null));
                }
                catch (Exception ex)
                {
                    _logger.Error($"批量打包失败：{game.Name}", ex);
                    items.Add(new BatchExportItem(game.Id, game.Name, false, 0, 0, ex.Message));
                }
            }
        }

        var result = new BatchExportResult(full, items);
        _logger.Info($"批量导出 zip 完成：{result.Summary} → {full}");
        return result;
    }

    /// <summary>子目录名：游戏名 + ID 前 8 位，同名游戏不会互相覆盖。</summary>
    private static string MakeFolderName(Game game, HashSet<string> used)
    {
        var safe = Sanitize(game.Name);
        if (safe.Length == 0)
        {
            safe = "game";
        }

        var candidate = $"{safe}_{game.Id.ToString("N")[..8]}";
        var unique = candidate;
        var suffix = 1;
        while (!used.Add(unique))
        {
            unique = $"{candidate}_{++suffix}";
        }

        return unique;
    }

    /// <summary>把文件名里的非法字符换成下划线，避免路径穿越与非法路径。</summary>
    internal static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '.' && builder.Length == 0 ? '_' : c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 解析游戏的存档根并复制文件。返回复制的文件数与字节数。
    /// 单个文件读失败只跳过该文件，不让整个游戏失败。
    /// </summary>
    private async Task<CopyOutcome> CopyGameFilesAsync(
        Game game,
        string targetRoot,
        CancellationToken ct)
    {
        _fileSystem.CreateDirectory(targetRoot);

        var fileCount = 0;
        var totalBytes = 0L;
        var hasRoot = false;

        foreach (var (root, rootName, absolute) in ResolveRoots(game))
        {
            hasRoot = true;
            var rootTarget = _fileSystem.Combine(targetRoot, rootName);
            _fileSystem.CreateDirectory(rootTarget);

            foreach (var (relative, absolutePath) in EnumerateFiles(root, absolute))
            {
                ct.ThrowIfCancellationRequested();

                var destination = _fileSystem.Combine(rootTarget, relative);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    _fileSystem.CreateDirectory(parent);
                }

                try
                {
                    using var source = _fileSystem.OpenRead(absolutePath);
                    using var target = _fileSystem.OpenCreate(destination);
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);

                    fileCount++;
                    totalBytes += _fileSystem.Stat(absolutePath).Size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"导出时跳过文件（{ex.GetType().Name}）：{absolutePath}", ex);
                }
            }
        }

        return new CopyOutcome(fileCount, totalBytes, hasRoot);
    }

    /// <summary>把游戏存档写进 zip，条目路径形如 "游戏名_xxxx/存档根名/相对路径"。</summary>
    private async Task<CopyOutcome> AddGameFilesToArchiveAsync(
        Game game,
        System.IO.Compression.ZipArchive archive,
        string folderName,
        CancellationToken ct)
    {
        var fileCount = 0;
        var totalBytes = 0L;
        var hasRoot = false;

        foreach (var (root, rootName, absolute) in ResolveRoots(game))
        {
            hasRoot = true;

            foreach (var (relative, absolutePath) in EnumerateFiles(root, absolute))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var entryName = $"{folderName}/{rootName}/{relative}".Replace('\\', '/');
                    var entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    using var source = _fileSystem.OpenRead(absolutePath);
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);

                    fileCount++;
                    totalBytes += _fileSystem.Stat(absolutePath).Size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"打包时跳过文件（{ex.GetType().Name}）：{absolutePath}", ex);
                }
            }
        }

        return new CopyOutcome(fileCount, totalBytes, hasRoot);
    }

    /// <summary>一次复制的统计：文件数、字节数、是否解析到过可用存档根。</summary>
    private readonly record struct CopyOutcome(int FileCount, long TotalBytes, bool HasResolvableRoot);

    /// <summary>
    /// 解析存档根。返回 (存档根定义, 用于目录名的根名, 绝对路径)。
    /// 解析不出来的根直接跳过 —— 导出不能因为一个失效路径就整体失败。
    /// </summary>
    private List<(SaveRoot Root, string Name, string AbsolutePath)> ResolveRoots(Game game)
    {
        var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
        var result = new List<(SaveRoot, string, string)>();

        foreach (var resolved in resolution.Roots)
        {
            if (resolved.AbsolutePath is null || !resolved.DirectoryExists)
            {
                continue;
            }

            result.Add((
                resolved.Root,
                MakeRootName(game, resolved.Index, resolved.Root.Token),
                resolved.AbsolutePath));
        }

        return result;
    }

    /// <summary>存档根的目录名：优先用路径末段，重名时补上序号。</summary>
    private static string MakeRootName(Game game, int index, string token)
    {
        var trimmed = token.TrimEnd('\\', '/');
        var leaf = trimmed.Length == 0 ? string.Empty : trimmed[(trimmed.LastIndexOf('\\') + 1)..];
        var safe = Sanitize(leaf);
        if (safe.Length == 0)
        {
            safe = "save" + (index + 1);
        }

        return $"{index + 1}_{safe}";
    }

    /// <summary>
    /// 枚举存档根下的文件，应用 include/exclude 规则。
    /// 返回 (相对路径, 绝对路径)。
    /// </summary>
    private IEnumerable<(string Relative, string Absolute)> EnumerateFiles(
        SaveRoot root,
        string absoluteRoot)
    {
        var matcher = new GlobMatcher(root.Include, root.Exclude);

        IReadOnlyList<string> paths;
        try
        {
            paths = _fileSystem.EnumerateFiles(absoluteRoot, root.Recursive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"导出时枚举目录失败，跳过该存档根：{absoluteRoot}", ex);
            yield break;
        }

        foreach (var path in paths)
        {
            string relative;
            try
            {
                relative = _fileSystem.GetRelativePath(absoluteRoot, path);
            }
            catch
            {
                continue;
            }

            if (relative.Length == 0 || !matcher.IsMatch(relative))
            {
                continue;
            }

            yield return (relative, path);
        }
    }
}
