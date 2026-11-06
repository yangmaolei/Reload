using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Restore;

/// <summary>接口签名已冻结（M2/M3 只增不改）。</summary>
public interface IRestoreService
{
    Task<RestorePlan> PlanAsync(Guid snapshotId, CancellationToken ct);

    Task<RestoreResult> ExecuteAsync(
        RestorePlan plan,
        IProgress<RestoreProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// 恢复引擎。顺序不可颠倒：
/// 1) 先做 pre_restore 快照（保证可逆）；
/// 2) 逐文件写临时文件 → 校验哈希 → MoveFileEx(REPLACE_EXISTING) 原子替换；
/// 3) 只读/隐藏属性先清除再复原；
/// 4) 被占用的文件重试 3 次，仍失败只记录，其余文件继续。
/// </summary>
public sealed class RestoreService : IRestoreService
{
    public const int MaxFileRetries = 3;
    private const FileAttributes ProtectionAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    private readonly IGameRepository _games;
    private readonly ISnapshotRepository _snapshots;
    private readonly IObjectStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IHashService _hashService;
    private readonly IPathTokenResolver _tokens;
    private readonly ISnapshotService _snapshotService;
    private readonly IAppLogger _logger;
    private readonly ISettingsRepository? _settings;

    public RestoreService(
        IGameRepository games,
        ISnapshotRepository snapshots,
        IObjectStore store,
        IFileSystem fileSystem,
        IHashService hashService,
        IPathTokenResolver tokens,
        ISnapshotService snapshotService,
        IAppLogger logger,
        ISettingsRepository? settings = null)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings;
    }

    /// <summary>是否把"快照里没有、当前目录里有"的文件一并删除（默认 true = 真正回到那一刻）。</summary>
    public bool DeleteExtraneousFiles { get; set; } = true;

    /// <summary>是否对每个恢复出来的文件调用 fsync（抗断电，代价约 6ms/文件）。</summary>
    public bool DurableWrites { get; set; }

    public async Task<RestorePlan> PlanAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"快照不存在：{snapshotId}");
        var game = await _games.GetAsync(snapshot.GameId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"游戏不存在：{snapshot.GameId}");
        var files = await _snapshots.GetFilesAsync(snapshotId, ct).ConfigureAwait(false);

        var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
        var unresolved = new List<string>();
        var rootPaths = new Dictionary<int, string>();

        foreach (var root in resolution.Roots)
        {
            if (root.AbsolutePath is not null && root.DirectoryExists)
            {
                rootPaths[root.Index] = root.AbsolutePath;
            }
        }

        if (resolution.MissingTokens.Count > 0)
        {
            unresolved.AddRange(resolution.MissingTokens);
        }

        foreach (var root in resolution.Roots)
        {
            if (!rootPaths.ContainsKey(root.Index) &&
                !unresolved.Contains(root.Root.Token, StringComparer.OrdinalIgnoreCase))
            {
                unresolved.Add(root.Root.Token);
            }
        }

        var operations = new List<RestoreOperation>(files.Count);
        var unchanged = 0;
        var manifestKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalExcludes = await LoadGlobalExcludesAsync(ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            manifestKeys.Add($"{file.RootIndex}|{file.RelPathKey}");

            if (!rootPaths.TryGetValue(file.RootIndex, out var rootPath))
            {
                // 该根的路径不可用：不猜测路径，只记录未解析
                continue;
            }

            var absolute = _fileSystem.Combine(rootPath, file.RelPath);

            // 路径穿越防护：清单来自远端，RelPath 可能带 "..\" 指到存档根之外。
            // 越界的条目直接跳过（绝不写出根目录），并记录告警。
            if (!IsInsideRoot(rootPath, absolute))
            {
                _logger.Warn($"清单条目越界，已跳过：{file.RelPath}");
                continue;
            }

            var exists = _fileSystem.FileExists(absolute);

            if (!exists)
            {
                operations.Add(new RestoreOperation(
                    RestoreOperationKind.Add, file.RootIndex, file.RelPath, absolute, file.Size, file.Hash, null));
                continue;
            }

            var stat = _fileSystem.Stat(absolute);
            if (stat.Size == file.Size)
            {
                var existingHash = await HashFileAsync(absolute, ct).ConfigureAwait(false);
                if (string.Equals(existingHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    unchanged++;
                    continue;
                }

                operations.Add(new RestoreOperation(
                    RestoreOperationKind.Overwrite, file.RootIndex, file.RelPath, absolute, file.Size, file.Hash,
                    existingHash));
                continue;
            }

            operations.Add(new RestoreOperation(
                RestoreOperationKind.Overwrite, file.RootIndex, file.RelPath, absolute, file.Size, file.Hash, null));
        }

        if (DeleteExtraneousFiles && unresolved.Count == 0)
        {
            foreach (var root in resolution.Roots)
            {
                if (!rootPaths.TryGetValue(root.Index, out var rootPath))
                {
                    continue;
                }

                // 排除规则必须与快照阶段一致：快照时跳过的文件（全局排除）
                // 同样不能在恢复时被当作"多余文件"删掉。
                var excludes = root.Root.Exclude.Count == 0
                    ? globalExcludes
                    : root.Root.Exclude.Concat(globalExcludes).ToList();
                var matcher = new GlobMatcher(root.Root.Include, excludes);
                IReadOnlyList<string> current;
                try
                {
                    current = _fileSystem.EnumerateFiles(rootPath, root.Root.Recursive);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"枚举目录失败，跳过删除计划：{rootPath}", ex);
                    continue;
                }

                foreach (var path in current)
                {
                    var relative = _fileSystem.GetRelativePath(rootPath, path);
                    if (!matcher.IsMatch(relative))
                    {
                        continue;
                    }

                    var key = $"{root.Index}|{relative.ToLowerInvariant()}";
                    if (manifestKeys.Contains(key))
                    {
                        continue;
                    }

                    var size = SafeSize(path);
                    operations.Add(new RestoreOperation(
                        RestoreOperationKind.Delete, root.Index, relative, path, size, null, null));
                }
            }
        }

        return new RestorePlan
        {
            SnapshotId = snapshot.Id,
            GameId = game.Id,
            GameName = game.Name,
            SnapshotParentId = snapshot.ParentId,
            SnapshotCreatedAt = snapshot.CreatedAt,
            Operations = operations,
            UnresolvedRoots = unresolved,
            UnchangedCount = unchanged,
        };
    }

    /// <summary>
    /// 判断目标路径是否落在存档根之内。远端清单里的相对路径可能带 "..\.."，
    /// 恢复时绝不能写到存档根之外。
    /// </summary>
    private bool IsInsideRoot(string rootPath, string absolute)
    {
        try
        {
            var root = Path.GetFullPath(rootPath);
            var target = Path.GetFullPath(absolute);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取设置页的"全局排除"（与快照阶段同一份设置、同一套解析规则）。
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadGlobalExcludesAsync(CancellationToken ct)
    {
        if (_settings is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var raw = await _settings.GetAsync(SnapshotService.GlobalExcludeKey, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn("读取全局排除失败，本次恢复按无排除处理", ex);
            return Array.Empty<string>();
        }
    }

    public async Task<RestoreResult> ExecuteAsync(
        RestorePlan plan,
        IProgress<RestoreProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanExecute)
        {
            throw new RestoreRequiresRelocateException(plan.UnresolvedRoots);
        }

        var startedAt = Environment.TickCount64;

        // 1) 恢复前保险点：这一步不可省略
        Guid? preRestoreId = null;
        try
        {
            var preRestore = await _snapshotService
                .CreateAsync(plan.GameId, SnapshotTrigger.PreRestore, $"恢复到 {plan.SnapshotId:N} 之前", ct)
                .ConfigureAwait(false);
            preRestoreId = preRestore.SnapshotId;
        }
        catch (NoFilesFoundException)
        {
            // 当前目录已经空了（例如用户手动删光），没有可保护的现场，继续恢复
            _logger.Warn("当前存档目录没有文件，跳过 pre_restore 快照");
        }

        var failures = new List<RestoreFailure>();
        var applied = 0;
        var deleted = 0;
        var total = plan.Operations.Count;
        var completed = 0;

        foreach (var operation in plan.Operations)
        {
            ct.ThrowIfCancellationRequested();
            completed++;
            progress?.Report(new RestoreProgress(completed, total, operation.RelPath, operation.Kind));

            try
            {
                switch (operation.Kind)
                {
                    case RestoreOperationKind.Delete:
                        DeleteFileWithRetry(operation);
                        deleted++;
                        break;

                    default:
                        await RestoreFileAsync(operation, ct).ConfigureAwait(false);
                        applied++;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }            catch (Exception ex)
            {
                _logger.Error($"恢复失败：{operation.RelPath}", ex);
                failures.Add(new RestoreFailure(operation.Kind, operation.RelPath, ex.Message));
            }
        }

        var elapsed = Environment.TickCount64 - startedAt;
        _logger.Info(
            $"恢复完成：写入 {applied}，删除 {deleted}，失败 {failures.Count}，耗时 {elapsed} ms" +
            (preRestoreId.HasValue ? $"，pre_restore={preRestoreId:N}" : "，无 pre_restore"));

        return new RestoreResult(plan.SnapshotId, preRestoreId, applied, deleted, failures, elapsed);
    }

    private async Task RestoreFileAsync(RestoreOperation operation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(operation.Hash))
        {
            throw new InvalidDataException("清单条目缺少哈希，无法恢复");
        }

        var destinationDirectory = Path.GetDirectoryName(operation.AbsolutePath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            _fileSystem.CreateDirectory(destinationDirectory);
        }

        var existingAttributes = _fileSystem.FileExists(operation.AbsolutePath)
            ? _fileSystem.Stat(operation.AbsolutePath).Attributes
            : (FileAttributes?)null;

        // 只读/隐藏/系统位要先清掉才能替换，替换后按"目标原有属性 ∪ 快照属性"复原
        if (existingAttributes.HasValue && (existingAttributes.Value & ProtectionAttributes) != 0)
        {
            _fileSystem.SetAttributes(operation.AbsolutePath, existingAttributes.Value & ~ProtectionAttributes);
        }

        var desiredAttributes = (FileAttributes)0;
        if (existingAttributes.HasValue)
        {
            desiredAttributes |= existingAttributes.Value & ProtectionAttributes;
        }

        var temp = Path.Combine(
            string.IsNullOrEmpty(destinationDirectory) ? "." : destinationDirectory,
            $".gssrestore-{Guid.NewGuid():N}.tmp");

        try
        {
            string actualHash;
            await using (var objectStream = _store.OpenReadAsync(operation.Hash, ct))
            await using (var destination = _fileSystem.OpenCreate(temp))
            {
                var buffer = new byte[1024 * 1024];
                using var hasher = _hashService.CreateHasher();
                int read;
                while ((read = await objectStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hasher.Update(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await destination.FlushAsync(ct).ConfigureAwait(false);
                if (DurableWrites && destination is FileStream fileStream)
                {
                    fileStream.Flush(true);
                }

                actualHash = hasher.Finalize();
            }

            if (!string.Equals(actualHash, operation.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"对象校验失败（期望 {operation.Hash}，实际 {actualHash}），已放弃写入该文件");
            }

            MoveWithRetry(temp, operation.AbsolutePath);
        }
        finally
        {
            TryDelete(temp);
        }

        if (desiredAttributes != 0)
        {
            _fileSystem.SetAttributes(operation.AbsolutePath, desiredAttributes);
        }
    }

    private void DeleteFileWithRetry(RestoreOperation operation)
    {
        for (var attempt = 1; attempt <= MaxFileRetries; attempt++)
        {
            try
            {
                if (!_fileSystem.FileExists(operation.AbsolutePath))
                {
                    return;
                }

                var attributes = _fileSystem.Stat(operation.AbsolutePath).Attributes;
                if ((attributes & ProtectionAttributes) != 0)
                {
                    _fileSystem.SetAttributes(operation.AbsolutePath, attributes & ~ProtectionAttributes);
                }

                _fileSystem.DeleteFile(operation.AbsolutePath);
                return;
            }
            catch (IOException) when (attempt < MaxFileRetries)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxFileRetries)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; attempt <= MaxFileRetries; attempt++)
        {
            try
            {
                _fileSystem.MoveFile(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MaxFileRetries)
                {
                    throw new IOException(
                        $"文件被占用，重试 {MaxFileRetries} 次仍失败（请先关闭游戏再试）：{destination}", ex);
                }

                Thread.Sleep(100 * attempt);
            }
        }
    }

    private long SafeSize(string path)
    {
        try
        {
            return _fileSystem.Stat(path).Size;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = _fileSystem.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        using var hasher = _hashService.CreateHasher();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
        }

        return hasher.Finalize();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch
        {
            // 临时文件清理失败不影响恢复结果
        }
    }
}
