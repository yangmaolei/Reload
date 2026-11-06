using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Covers;

/// <summary>封面下载抽象：Core 只负责缓存与命名，真正发请求的实现放在 App（HttpClient）。</summary>
public interface ICoverDownloader
{
    Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct);
}

/// <summary>
/// 封面缓存：Steam 的公开 CDN 头图（按 AppId），下不到就返回 null，UI 用首字母色块兜底。
/// 封面纯属装饰，任何失败都不得影响备份主流程。
/// </summary>
public sealed class CoverService
{
    private static readonly Uri SteamCdnBase = new("https://cdn.cloudflare.steamstatic.com/steam/apps/");

    private readonly AppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly ICoverDownloader? _downloader;
    private readonly IAppLogger _logger;

    public CoverService(
        AppPaths paths,
        IFileSystem fileSystem,
        ICoverDownloader? downloader,
        IAppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _downloader = downloader;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string CoversDirectory => Path.Combine(_paths.DataRoot, "covers");

    /// <summary>Steam 竖版海报（600×900，游戏立绘居中，裁成方形封面效果最好）。</summary>
    public static Uri? BuildSteamPortraitUri(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
        {
            return null;
        }

        return new Uri(SteamCdnBase, $"{appId}/library_600x900.jpg");
    }

    /// <summary>Steam 横版头图（460×215），竖版下不到的兜底。</summary>
    public static Uri? BuildSteamCoverUri(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
        {
            return null;
        }

        return new Uri(SteamCdnBase, $"{appId}/header.jpg");
    }

    public string? GetCachedPath(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var path = BuildCachePath(game);
        return path is not null && _fileSystem.FileExists(path) ? path : null;
    }

    public async Task<string?> GetAsync(Game game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);

        var cached = GetCachedPath(game);
        if (cached is not null)
        {
            return cached;
        }

        if (_downloader is null)
        {
            return null;
        }

        // 优先竖版海报，下不到再退横版头图（都失败则用首字母色块）
        var candidates = game.Source == GameSource.Steam
            ? new[] { BuildSteamPortraitUri(game.ExternalId), BuildSteamCoverUri(game.ExternalId) }
            : Array.Empty<Uri?>();

        foreach (var uri in candidates)
        {
            if (uri is null)
            {
                continue;
            }

            var target = await TryDownloadAsync(game, uri, ct).ConfigureAwait(false);
            if (target is not null)
            {
                return target;
            }
        }

        return null;
    }

    private async Task<string?> TryDownloadAsync(Game game, Uri uri, CancellationToken ct)
    {
        if (_downloader is null)
        {
            return null;
        }

        try
        {
            var bytes = await _downloader.DownloadAsync(uri, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var target = BuildCachePath(game);
            if (target is null)
            {
                return null;
            }

            _fileSystem.CreateDirectory(CoversDirectory);
            var temp = target + ".part";
            using (var stream = _fileSystem.OpenCreate(temp))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            _fileSystem.MoveFile(temp, target, overwrite: true);
            _logger.Debug($"封面已缓存：{game.Name}");
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug($"封面下载失败（忽略）：{game.Name} / {ex.Message}");
            return null;
        }
    }

    private string? BuildCachePath(Game game)
    {
        var key = game.Source == GameSource.Steam && !string.IsNullOrWhiteSpace(game.ExternalId)
            ? $"steam-{game.ExternalId}"
            : null;

        if (key is null || !key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        return Path.Combine(CoversDirectory, key + ".jpg");
    }
}
