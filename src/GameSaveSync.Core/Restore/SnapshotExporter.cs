using System.IO.Compression;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Restore;

/// <summary>把快照导出成 zip（对象从本地 CAS 读，路径按相对存档根还原）。</summary>
public interface ISnapshotExporter
{
    Task<string> ExportAsync(Guid snapshotId, string destinationZipPath, CancellationToken ct);
}

public sealed class SnapshotExporter : ISnapshotExporter
{
    private readonly ISnapshotRepository _snapshots;
    private readonly IGameRepository _games;
    private readonly IObjectStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IAppLogger _logger;

    public SnapshotExporter(
        ISnapshotRepository snapshots,
        IGameRepository games,
        IObjectStore store,
        IFileSystem fileSystem,
        IAppLogger logger)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExportAsync(Guid snapshotId, string destinationZipPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var snapshot = await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"快照不存在：{snapshotId}");
        var game = await _games.GetAsync(snapshot.GameId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"游戏不存在：{snapshot.GameId}");
        var files = await _snapshots.GetFilesAsync(snapshotId, ct).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(destinationZipPath))
        {
            File.Delete(destinationZipPath);
        }

        var missing = new List<string>();
        using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (!_store.Contains(file.Hash))
                {
                    missing.Add(file.RelPath);
                    continue;
                }

                var entryName = files.Count(f => f.RootIndex == file.RootIndex) == files.Count
                    ? file.RelPath
                    : $"{file.RootIndex}/{file.RelPath}";

                var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
                await using var target = entry.Open();
                await using var source = _store.OpenReadAsync(file.Hash, ct);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
        }

        if (missing.Count > 0)
        {
            _logger.Warn($"导出 zip 时缺少 {missing.Count} 个本地对象：{string.Join("、", missing.Take(5))}");
        }

        _logger.Info($"快照 {snapshotId:N} 已导出到 {destinationZipPath}（{files.Count - missing.Count} 个文件）");
        return destinationZipPath;
    }
}
