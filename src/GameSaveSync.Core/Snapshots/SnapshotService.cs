using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Detection;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Hashing;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Snapshots;

/// <summary>接口签名已冻结（M2/M3 只增不改）。</summary>
public interface ISnapshotService
{
    Task<SnapshotResult> CreateAsync(
        Guid gameId,
        SnapshotTrigger trigger,
        string? note,
        CancellationToken ct);
}

/// <summary>
/// 快照引擎：递归枚举 → （L2 指纹快路径）→ 逐文件 BLAKE3 → 内容寻址落盘 → 写清单 → 入库。
/// M2 增加两件事：
/// 1) L2 快速路径：size/mtime/文件 ID 都没变时直接复用上次哈希，不重复读盘；
/// 2) 内容与上一快照完全一致时**不产生新快照**（<see cref="SkipIfUnchangedWithParent"/>）。
/// 单文件失败只跳过并记录，绝不让整个快照失败；一个文件都没有时绝不生成空快照。
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    /// <summary>设置表里存全局排除的键（每行一个通配符）。</summary>
    public const string GlobalExcludeKey = "snapshot.global_excludes";

    /// <summary>稳定性重试次数（规格：最多 3 次）。</summary>
    public const int MaxStabilityAttempts = 3;

    private readonly IGameRepository _games;
    private readonly ISnapshotRepository _snapshots;
    private readonly IObjectRepository _objects;
    private readonly IObjectStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IPathTokenResolver _tokens;
    private readonly ISettingsRepository _settings;
    private readonly ISnapshotManifestStore _manifestStore;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly IFingerprintStore? _fingerprints;
    private readonly IFileIdentityProvider? _fileIdentity;
    private readonly IHashService _hashService;

    public SnapshotService(
        IGameRepository games,
        ISnapshotRepository snapshots,
        IObjectRepository objects,
        IObjectStore store,
        IFileSystem fileSystem,
        IPathTokenResolver tokens,
        ISettingsRepository settings,
        ISnapshotManifestStore manifestStore,
        IAppLogger logger,
        IClock clock,
        IFingerprintStore? fingerprints = null,
        IFileIdentityProvider? fileIdentity = null,
        IHashService? hashService = null)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fingerprints = fingerprints;
        _fileIdentity = fileIdentity;
        _hashService = hashService ?? Blake3HashService.Instance;
    }

    /// <summary>M2 的 idle 快照用节流参数（1 MB 块 + 50 ms 间隔）；手动路径不节流。</summary>
    public HashIoOptions IoOptions { get; set; } = HashIoOptions.Default;

    /// <summary>内容与上一快照完全一致时不落库（后台触发用；手动快照保持"点了就有"）。</summary>
    public bool SkipIfUnchangedWithParent { get; set; }

    /// <summary>L2 快速路径开关（测试可关掉以强制全量哈希）。</summary>
    public bool UseMetadataFastPath { get; set; } = true;

    public async Task<SnapshotResult> CreateAsync(
        Guid gameId,
        SnapshotTrigger trigger,
        string? note,
        CancellationToken ct)
    {

        // 设置页的"全局排除"：每次快照现读（低频操作，一次 SELECT 可忽略），改完无需重启
        try
        {
            var raw = await _settings.GetAsync(GlobalExcludeKey, ct).ConfigureAwait(false);
            _activeGlobalExcludes = string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
        }
        catch
        {
            _activeGlobalExcludes = Array.Empty<string>();
        }
        var game = await _games.GetAsync(gameId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"游戏不存在：{gameId}");

        var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);

        if (resolution.MissingTokens.Count > 0)
        {
            await _games.SetNeedsRelocateAsync(game.Id, true, ct).ConfigureAwait(false);
            _logger.Warn($"游戏《{game.Name}》存在无法解析的路径令牌：{string.Join("、", resolution.MissingTokens)}");
            throw new NeedsRelocateException(game.Id, resolution.MissingTokens);
        }

        var unavailable = resolution.UnavailableRoots;
        var partiallyUnavailable = unavailable.Count > 0;
        if (unavailable.Count > 0)
        {
            await _games.SetNeedsRelocateAsync(game.Id, true, ct).ConfigureAwait(false);
            foreach (var root in unavailable)
            {
                _logger.Warn($"存档目录不存在：{root.Root.Token} → {root.AbsolutePath ?? "(未解析)"}");
            }

            if (unavailable.Count == resolution.Roots.Count)
            {
                throw new NeedsRelocateException(game.Id, Array.Empty<string>());
            }
        }

        var candidates = EnumerateCandidates(game, resolution);
        if (candidates.Count == 0)
        {
            _logger.Warn($"游戏《{game.Name}》没有扫描到文件，放弃创建快照（不产生空快照）");
            throw new NoFilesFoundException(game.Id);
        }

        var parent = await _snapshots.GetLatestAsync(game.Id, ct).ConfigureAwait(false);
        var parentFiles = parent is null
            ? new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase)
            : (await _snapshots.GetFilesAsync(parent.Id, ct).ConfigureAwait(false))
                .ToDictionary(f => FingerprintKeys.Build(f.RootIndex, f.RelPathKey), StringComparer.OrdinalIgnoreCase);

        var previousFingerprints = _fingerprints is null
            ? new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase)
            : await _fingerprints.LoadAsync(game.Id, ct).ConfigureAwait(false);

        var deviceId = await _settings.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var createdAt = _clock.UtcNowMs;
        var snapshotId = Guid.NewGuid();

        var manifest = new SnapshotManifest
        {
            SnapshotId = snapshotId,
            GameId = game.Id,
            GameName = game.Name,
            CreatedAt = createdAt,
            Trigger = trigger.ToDbValue(),
            DeviceId = deviceId,
            Note = note,
            ParentId = parent?.Id,
        };

        var files = new List<SnapshotFile>(candidates.Count);
        var fingerprints = new List<FingerprintRecord>(candidates.Count);
        var skipped = new List<SkippedFileInfo>();
        var newObjects = 0;
        var reusedObjects = 0;
        var fastPathHits = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var key = FingerprintKeys.Build(candidate.RootIndex, candidate.RelPath.ToLowerInvariant());

            if (TryReuseFromFastPath(candidate, key, parentFiles, previousFingerprints, out var reused))
            {
                fastPathHits++;
                reusedObjects++;
                files.Add(new SnapshotFile
                {
                    SnapshotId = snapshotId,
                    RelPath = candidate.RelPath,
                    RelPathKey = key[(key.IndexOf('|') + 1)..],
                    Hash = reused!.Value.Hash,
                    Size = reused.Value.Size,
                    Mtime = reused.Value.MtimeMs,
                    Attrs = (int)reused.Value.Attrs,
                    RootIndex = candidate.RootIndex,
                });
                fingerprints.Add(new FingerprintRecord(
                    candidate.RootIndex,
                    key[(key.IndexOf('|') + 1)..],
                    reused.Value.Size,
                    reused.Value.MtimeMs,
                    reused.Value.VolumeSerial,
                    reused.Value.FileId,
                    reused.Value.Hash));
                continue;
            }

            var outcome = await CaptureFileAsync(candidate, ct).ConfigureAwait(false);
            if (outcome.Skipped is not null)
            {
                skipped.Add(outcome.Skipped);
                manifest.Skipped.Add(new ManifestSkipped
                {
                    Root = candidate.RootIndex,
                    RelPath = candidate.RelPath,
                    Reason = outcome.Skipped.Reason,
                });
                continue;
            }

            if (outcome.IsNewObject)
            {
                newObjects++;
            }
            else
            {
                reusedObjects++;
            }

            var relPathKey = candidate.RelPath.ToLowerInvariant();
            files.Add(new SnapshotFile
            {
                SnapshotId = snapshotId,
                RelPath = candidate.RelPath,
                RelPathKey = relPathKey,
                Hash = outcome.Hash!,
                Size = outcome.Size,
                Mtime = outcome.MtimeMs,
                Attrs = (int)outcome.Attributes,
                RootIndex = candidate.RootIndex,
            });

            fingerprints.Add(new FingerprintRecord(
                candidate.RootIndex,
                relPathKey,
                outcome.Size,
                outcome.MtimeMs,
                outcome.VolumeSerial,
                outcome.FileId,
                outcome.Hash!));
        }

        if (files.Count == 0)
        {
            _logger.Error($"游戏《{game.Name}》所有文件都被跳过，放弃创建快照");
            throw new NoFilesFoundException(game.Id);
        }

        files.Sort(static (a, b) =>
        {
            var byRoot = a.RootIndex.CompareTo(b.RootIndex);
            return byRoot != 0 ? byRoot : string.CompareOrdinal(a.RelPathKey, b.RelPathKey);
        });

        if (SkipIfUnchangedWithParent && parent is not null && IsSameContent(files, parentFiles))
        {
            _logger.Debug($"游戏《{game.Name}》内容与上一快照一致，跳过创建（{trigger.ToDisplayName()}）");
            return new SnapshotResult(
                game.Id,
                null,
                Created: false,
                SkipReason: "内容与上一快照完全一致，未产生新快照",
                FileCount: files.Count,
                TotalSize: files.Sum(f => f.Size),
                NewObjects: 0,
                ReusedObjects: reusedObjects,
                Skipped: skipped);
        }

        manifest.FileCount = files.Count;
        manifest.TotalSize = files.Sum(f => f.Size);
        manifest.Roots = resolution.Roots
            .Select((root, index) => new ManifestRoot
            {
                Index = index,
                Token = root.Root.Token,
                IsTokenized = root.Root.IsTokenized,
            })
            .ToList();
        manifest.Files = files
            .Select(f => new ManifestFile
            {
                Root = f.RootIndex,
                RelPath = f.RelPath,
                Hash = f.Hash,
                Size = f.Size,
                Mtime = f.Mtime,
                Attrs = f.Attrs,
            })
            .ToList();

        var manifestHash = SnapshotManifestSerializer.ComputeHash(manifest, _hashService);

        var snapshot = new Snapshot
        {
            Id = snapshotId,
            GameId = game.Id,
            ParentId = parent?.Id,
            CreatedAt = createdAt,
            Trigger = trigger,
            DeviceId = deviceId,
            ManifestHash = manifestHash,
            TotalSize = manifest.TotalSize,
            FileCount = files.Count,
            Note = note,
            LocalState = LocalSnapshotState.Present,
            RemoteState = RemoteSnapshotState.Pending,
            SkippedCount = skipped.Count,
        };

        await _snapshots.InsertAsync(snapshot, files, ct).ConfigureAwait(false);

        await _objects.RegisterManyAsync(
            files.Select(f => (f.Hash, f.Size)).ToArray(), ct).ConfigureAwait(false);

        _manifestStore.Save(game.Id, snapshot, manifest);

        if (_fingerprints is not null)
        {
            await _fingerprints.SaveAsync(game.Id, fingerprints, ct).ConfigureAwait(false);
        }

        // 之前被标记"需重新定位"的游戏，只要这次能完整跑通就自动清标记
        if (game.NeedsRelocate && !partiallyUnavailable)
        {
            await _games.SetNeedsRelocateAsync(game.Id, false, ct).ConfigureAwait(false);
        }

        _logger.Info(
            $"已创建快照 {SnapshotNames.ManifestFileName(snapshot)}（{trigger.ToDisplayName()}）：" +
            $"{files.Count} 个文件，{manifest.TotalSize} 字节，新增对象 {newObjects}，复用 {reusedObjects}" +
            $"（其中指纹命中 {fastPathHits}），跳过 {skipped.Count}");

        return new SnapshotResult(
            game.Id,
            snapshotId,
            Created: true,
            SkipReason: null,
            FileCount: files.Count,
            TotalSize: manifest.TotalSize,
            NewObjects: newObjects,
            ReusedObjects: reusedObjects,
            Skipped: skipped);
    }

    /// <summary>L2：size + mtime + 文件 ID 都没变 → 直接复用上次的哈希，不读盘。</summary>
    private bool TryReuseFromFastPath(
        FileCandidate candidate,
        string key,
        IReadOnlyDictionary<string, SnapshotFile> parentFiles,
        IReadOnlyDictionary<string, FileFingerprint> fingerprints,
        out ReusedFile? reused)
    {
        reused = null;

        if (!UseMetadataFastPath ||
            !parentFiles.TryGetValue(key, out var parentFile) ||
            !fingerprints.TryGetValue(key, out var fingerprint) ||
            string.IsNullOrEmpty(fingerprint.Hash))
        {
            return false;
        }

        FileEntry stat;
        try
        {
            stat = _fileSystem.Stat(candidate.AbsolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (stat.Size != parentFile.Size || stat.MtimeMs != parentFile.Mtime)
        {
            return false;
        }

        var identity = _fileIdentity?.TryGet(candidate.AbsolutePath) ?? FileIdentity.None;
        if (identity.IsKnown && fingerprint.FileId != 0 && identity.FileId != fingerprint.FileId)
        {
            // 同 size/mtime 但文件 ID 变了（改名/替换）→ 必须重新哈希
            return false;
        }

        reused = new ReusedFile(
            parentFile.Hash,
            parentFile.Size,
            parentFile.Mtime,
            (FileAttributes)parentFile.Attrs,
            identity.IsKnown ? identity.VolumeSerial : fingerprint.VolumeSerial,
            identity.IsKnown ? identity.FileId : fingerprint.FileId);
        return true;
    }

    private static bool IsSameContent(
        IReadOnlyList<SnapshotFile> current,
        IReadOnlyDictionary<string, SnapshotFile> parentFiles)
    {
        if (current.Count != parentFiles.Count)
        {
            return false;
        }

        foreach (var file in current)
        {
            var key = FingerprintKeys.Build(file.RootIndex, file.RelPathKey);
            if (!parentFiles.TryGetValue(key, out var parentFile) ||
                !string.Equals(parentFile.Hash, file.Hash, StringComparison.OrdinalIgnoreCase) ||
                parentFile.Size != file.Size)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>本次快照生效的全局排除（设置页可配，每行一个通配符）。</summary>
    private IReadOnlyList<string> _activeGlobalExcludes = Array.Empty<string>();

    private List<FileCandidate> EnumerateCandidates(Game game, SaveRootResolution resolution)
    {
        var candidates = new List<FileCandidate>();

        foreach (var root in resolution.Roots)
        {
            if (root.AbsolutePath is null || !root.DirectoryExists)
            {
                continue;
            }

            var excludes = root.Root.Exclude.Count == 0
                ? _activeGlobalExcludes
                : root.Root.Exclude.Concat(_activeGlobalExcludes).ToList();
            var matcher = new GlobMatcher(root.Root.Include, excludes);
            IReadOnlyList<string> paths;
            try
            {
                paths = _fileSystem.EnumerateFiles(root.AbsolutePath, root.Root.Recursive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"枚举目录失败，跳过该存档根：{root.AbsolutePath}", ex);
                continue;
            }

            foreach (var path in paths)
            {
                var relative = _fileSystem.GetRelativePath(root.AbsolutePath, path);
                if (relative.Length == 0 || !matcher.IsMatch(relative))
                {
                    continue;
                }

                candidates.Add(new FileCandidate(root.Index, path, relative));
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var byRoot = a.RootIndex.CompareTo(b.RootIndex);
            return byRoot != 0
                ? byRoot
                : string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase);
        });

        _logger.Debug($"游戏《{game.Name}》扫描到 {candidates.Count} 个候选文件");
        return candidates;
    }

    /// <summary>
    /// 读文件前后各 stat 一次：size 与 mtime 都没变才认为内容稳定。
    /// 不稳定则重读，最多 <see cref="MaxStabilityAttempts"/> 次，仍不稳定就跳过该文件。
    /// </summary>
    private async Task<CaptureOutcome> CaptureFileAsync(FileCandidate candidate, CancellationToken ct)
    {
        var reason = "文件在读取期间持续变化";

        for (var attempt = 1; attempt <= MaxStabilityAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            FileEntry before;
            StagedObject? staged = null;
            try
            {
                before = _fileSystem.Stat(candidate.AbsolutePath);
                if (before.IsDirectory)
                {
                    return CaptureOutcome.ForSkipped(
                        new SkippedFileInfo(candidate.RootIndex, candidate.RelPath, "不是普通文件"));
                }

                staged = await _store.StageFromFileAsync(candidate.AbsolutePath, IoOptions, ct).ConfigureAwait(false);
                var after = _fileSystem.Stat(candidate.AbsolutePath);

                if (!before.MetadataEquals(after))
                {
                    staged.Dispose();
                    staged = null;
                    reason = $"文件在读取期间变化（{before.Size}/{after.Size} 字节）";
                    await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
                    continue;
                }

                var isNew = _store.Commit(staged);
                var hash = staged.Hash;
                var size = staged.Size;
                staged.Committed = true;
                staged.Dispose();

                var identity = _fileIdentity?.TryGet(candidate.AbsolutePath) ?? FileIdentity.None;

                return CaptureOutcome.Success(
                    hash, size, after.MtimeMs, after.Attributes, isNew, identity.VolumeSerial, identity.FileId);
            }
            catch (OperationCanceledException)
            {
                staged?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                staged?.Dispose();
                _logger.Warn($"跳过文件（{ex.GetType().Name}）：{candidate.AbsolutePath}", ex);
                return CaptureOutcome.ForSkipped(new SkippedFileInfo(
                    candidate.RootIndex,
                    candidate.RelPath,
                    ex is UnauthorizedAccessException ? "无访问权限" : "文件被占用或读取失败"));
            }
        }

        _logger.Warn($"跳过文件（{reason}）：{candidate.AbsolutePath}");
        return CaptureOutcome.ForSkipped(new SkippedFileInfo(candidate.RootIndex, candidate.RelPath, reason));
    }

    private sealed record FileCandidate(int RootIndex, string AbsolutePath, string RelPath);

    private readonly record struct ReusedFile(
        string Hash,
        long Size,
        long MtimeMs,
        FileAttributes Attrs,
        ulong VolumeSerial,
        ulong FileId);

    private sealed record CaptureOutcome(
        string? Hash,
        long Size,
        long MtimeMs,
        FileAttributes Attributes,
        bool IsNewObject,
        ulong VolumeSerial,
        ulong FileId,
        SkippedFileInfo? Skipped)
    {
        public static CaptureOutcome Success(
            string hash, long size, long mtimeMs, FileAttributes attributes, bool isNewObject,
            ulong volumeSerial, ulong fileId) =>
            new(hash, size, mtimeMs, attributes, isNewObject, volumeSerial, fileId, null);

        public static CaptureOutcome ForSkipped(SkippedFileInfo info) =>
            new(null, 0, 0, default, false, 0, 0, info);
    }
}

public static class SnapshotNames
{
    /// <summary>远端清单文件名：<c>&lt;ts&gt;-&lt;sid&gt;.json</c>。</summary>
    public static string ManifestFileName(Snapshot snapshot) =>
        ManifestFileName(snapshot.CreatedAt, snapshot.Id);

    public static string ManifestFileName(long createdAt, Guid snapshotId) =>
        $"{TimeUtil.SnapshotFilePrefix(createdAt)}-{snapshotId:N}.json";
}
