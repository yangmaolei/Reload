using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.Core.Remote;

/// <summary>
/// 把远端某个快照"落地"到本机：拉清单 → 建/补游戏 → 下载缺失对象（逐个校验 BLAKE3）→ 写库。
/// 只有这里会去远端拉对象；日常恢复仍然只读本地 CAS。
/// </summary>
public interface IRemoteSnapshotImporter
{
    /// <summary>返回 true 表示本次真的导入了（此前本机没有这个快照）。</summary>
    Task<bool> ImportAsync(Guid gameId, Guid snapshotId, CancellationToken ct);
}

public sealed class RemoteSnapshotImporter : IRemoteSnapshotImporter
{
    private readonly IRemoteStore _remote;
    private readonly ISnapshotRepository _snapshots;
    private readonly IGameRepository _games;
    private readonly IObjectStore _store;
    private readonly IObjectRepository _objects;
    private readonly ISnapshotManifestStore _manifests;
    private readonly IHashService _hashService;
    private readonly IAppLogger _logger;

    public RemoteSnapshotImporter(
        IRemoteStore remote,
        ISnapshotRepository snapshots,
        IGameRepository games,
        IObjectStore store,
        IObjectRepository objects,
        ISnapshotManifestStore manifests,
        IHashService hashService,
        IAppLogger logger)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> ImportAsync(Guid gameId, Guid snapshotId, CancellationToken ct)
    {
        if (await _snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }

        var fileName = await FindManifestFileNameAsync(gameId, snapshotId, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException(
                           $"远端没有快照 {snapshotId:N} 的清单（games/{gameId:D}/snapshots）");

        var json = await _remote.GetManifestJsonAsync(gameId, fileName, ct).ConfigureAwait(false)
                   ?? throw new FileNotFoundException($"清单读取失败：{fileName}");

        var manifest = SnapshotManifestSerializer.FromJson(json);
        if (manifest.SnapshotId != snapshotId)
        {
            throw new InvalidDataException(
                $"清单内容与索引不一致：期望 {snapshotId:N}，实际 {manifest.SnapshotId:N}");
        }

        // 清单里的 gameId 必须与索引指向的一致：
        // 不一致说明清单被错配/篡改，导入后快照会挂到另一个游戏下，索引指向空游戏。
        if (manifest.GameId != Guid.Empty && manifest.GameId != gameId)
        {
            throw new InvalidDataException(
                $"清单的游戏与索引不一致：索引指向 {gameId:N}，清单属于 {manifest.GameId:N}");
        }

        var game = await EnsureGameAsync(manifest, ct).ConfigureAwait(false);

        var downloaded = 0;
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (_store.Contains(file.Hash))
            {
                continue;
            }

            await using var stream = await _remote.GetObjectAsync(file.Hash, ct).ConfigureAwait(false)
                                     ?? throw new FileNotFoundException($"远端缺少对象：{file.Hash}");

            // PutAsync 内部会校验内容哈希，截断/篡改会直接抛 InvalidDataException
            await _store.PutAsync(file.Hash, stream, ct).ConfigureAwait(false);
            downloaded++;
        }

        var snapshot = new Snapshot
        {
            Id = manifest.SnapshotId,
            GameId = manifest.GameId == Guid.Empty ? gameId : manifest.GameId,
            ParentId = manifest.ParentId,
            CreatedAt = manifest.CreatedAt,
            Trigger = SnapshotTriggerNames.Parse(manifest.Trigger),
            DeviceId = manifest.DeviceId,
            ManifestHash = SnapshotManifestSerializer.ComputeHash(manifest, _hashService),
            TotalSize = manifest.TotalSize,
            FileCount = manifest.FileCount,
            Note = manifest.Note,
            LocalState = LocalSnapshotState.Present,
            RemoteState = RemoteSnapshotState.Uploaded,
            SkippedCount = manifest.Skipped.Count,
        };

        var files = manifest.Files
            .Select(f => new SnapshotFile
            {
                SnapshotId = snapshot.Id,
                RelPath = f.RelPath,
                RelPathKey = f.RelPath.ToLowerInvariant(),
                Hash = f.Hash,
                Size = f.Size,
                Mtime = f.Mtime,
                Attrs = f.Attrs,
                RootIndex = f.Root,
            })
            .ToList();

        if (await _snapshots.GetAsync(snapshot.Id, ct).ConfigureAwait(false) is null)
        {
            await _snapshots.InsertAsync(snapshot, files, ct).ConfigureAwait(false);
            await _objects.RegisterManyAsync(
                files.Select(f => (f.Hash, f.Size)).ToArray(), ct).ConfigureAwait(false);

            foreach (var hash in files.Select(f => f.Hash).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await _objects.MarkUploadedAsync(hash, ct).ConfigureAwait(false);
            }

            _manifests.Save(snapshot.GameId, snapshot, manifest);
        }

        _logger.Info(
            $"已从远端导入快照 {fileName}（设备 {manifest.DeviceId[..Math.Min(8, manifest.DeviceId.Length)]}…）：" +
            $"{snapshot.FileCount} 个文件，新下载对象 {downloaded} 个");

        return true;
    }

    /// <summary>清单文件名里带时间前缀，只能按目录列举 + sid 匹配（不做逐文件 PROPFIND）。</summary>
    private async Task<string?> FindManifestFileNameAsync(Guid gameId, Guid snapshotId, CancellationToken ct)
    {
        var names = await _remote.ListAsync($"games/{gameId:D}/snapshots", ct).ConfigureAwait(false);
        var suffix = $"-{snapshotId:N}.json";

        return names.FirstOrDefault(name =>
            name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>本机还没有这个游戏时，按清单里的令牌化存档根建一条（不猜绝对路径）。</summary>
    private async Task<Game> EnsureGameAsync(SnapshotManifest manifest, CancellationToken ct)
    {
        var existing = await _games.GetAsync(manifest.GameId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var game = new Game
        {
            Id = manifest.GameId,
            Name = string.IsNullOrWhiteSpace(manifest.GameName) ? "远端游戏" : manifest.GameName,
            Source = GameSource.Manual,
            Enabled = true,
            NeedsRelocate = true,      // 新机器上尚未验证过路径
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var root in manifest.Roots.OrderBy(r => r.Index))
        {
            game.SaveRoots.Add(new SaveRoot
            {
                Token = root.Token,
                IsTokenized = root.IsTokenized,
            });
        }

        await _games.UpsertAsync(game, ct).ConfigureAwait(false);
        _logger.Info($"从远端索引新建游戏：{game.Name}（{game.SaveRoots.Count} 个存档根，等待本机定位）");
        return game;
    }
}
