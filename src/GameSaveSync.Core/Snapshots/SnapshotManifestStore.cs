using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Snapshots;

/// <summary>
/// 清单的本地副本读写。本地文件是"可读副本"，DB 是权威源；两者不一致时回落到 DB 重建。
/// 全部走 <see cref="IFileSystem"/>，保证长路径与 \\?\ 前缀一致。
/// </summary>
public interface ISnapshotManifestStore
{
    void Save(Guid gameId, Snapshot snapshot, SnapshotManifest manifest);

    Task<SnapshotManifest> LoadAsync(Snapshot snapshot, CancellationToken ct);

    /// <summary>删除本地清单副本（保留策略清理时调用）。</summary>
    void Delete(Guid gameId, string fileName);
}

public sealed class SnapshotManifestStore : ISnapshotManifestStore
{
    private readonly AppPaths _paths;
    private readonly ISnapshotRepository _snapshots;
    private readonly IGameRepository _games;
    private readonly IFileSystem _fileSystem;
    private readonly IAppLogger _logger;

    public SnapshotManifestStore(
        AppPaths paths,
        ISnapshotRepository snapshots,
        IGameRepository games,
        IFileSystem fileSystem,
        IAppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Save(Guid gameId, Snapshot snapshot, SnapshotManifest manifest)
    {
        try
        {
            var path = _paths.ManifestPath(gameId.ToString("D"), SnapshotNames.ManifestFileName(snapshot));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                _fileSystem.CreateDirectory(directory);
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(SnapshotManifestSerializer.ToJson(manifest));
            using var stream = _fileSystem.OpenCreate(path);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            _logger.Warn("写入本地清单副本失败", ex);
        }
    }

    public async Task<SnapshotManifest> LoadAsync(Snapshot snapshot, CancellationToken ct)
    {
        var path = _paths.ManifestPath(
            snapshot.GameId.ToString("D"),
            SnapshotNames.ManifestFileName(snapshot));

        if (_fileSystem.FileExists(path))
        {
            try
            {
                using var stream = _fileSystem.OpenRead(path);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                return SnapshotManifestSerializer.FromJson(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"本地清单损坏，改为从数据库重建：{path}", ex);
            }
        }

        return await BuildFromDatabaseAsync(snapshot, ct).ConfigureAwait(false);
    }

    public void Delete(Guid gameId, string fileName)
    {
        try
        {
            var path = _paths.ManifestPath(gameId.ToString("D"), fileName);
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"删除本地清单失败：{fileName}", ex);
        }
    }

    private async Task<SnapshotManifest> BuildFromDatabaseAsync(Snapshot snapshot, CancellationToken ct)
    {
        var game = await _games.GetAsync(snapshot.GameId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"游戏不存在：{snapshot.GameId}");
        var files = await _snapshots.GetFilesAsync(snapshot.Id, ct).ConfigureAwait(false);

        return new SnapshotManifest
        {
            SnapshotId = snapshot.Id,
            GameId = snapshot.GameId,
            GameName = game.Name,
            CreatedAt = snapshot.CreatedAt,
            Trigger = snapshot.Trigger.ToDbValue(),
            DeviceId = snapshot.DeviceId,
            ParentId = snapshot.ParentId,
            Note = snapshot.Note,
            FileCount = snapshot.FileCount,
            TotalSize = snapshot.TotalSize,
            Roots = game.SaveRoots
                .Select((root, index) => new ManifestRoot
                {
                    Index = index,
                    Token = root.Token,
                    IsTokenized = root.IsTokenized,
                })
                .ToList(),
            Files = files
                .Select(f => new ManifestFile
                {
                    Root = f.RootIndex,
                    RelPath = f.RelPath,
                    Hash = f.Hash,
                    Size = f.Size,
                    Mtime = f.Mtime,
                    Attrs = f.Attrs,
                })
                .ToList(),
        };
    }
}
