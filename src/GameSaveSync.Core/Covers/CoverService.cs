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
/// 封面缓存：Steam 的公开 CDN 主视觉（按 AppId），下不到就返回 null，UI 用首字母色块兜底。
/// 封面纯属装饰，任何失败都不得影响备份主流程。
/// <para>
/// 同一款游戏有两种形态（竖屏长图 / 方形头图），各自独立缓存：
/// 文件名 <c>steam-&lt;appId&gt;-p.jpg</c> 与 <c>steam-&lt;appId&gt;-s.jpg</c>。
/// </para>
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

    /// <summary>Steam 竖屏长图（600×900，2:3），适合大尺寸主视觉。</summary>
    public static Uri? BuildSteamPortraitUri(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
        {
            return null;
        }

        return new Uri(SteamCdnBase, $"{appId}/library_600x900.jpg");
    }

    /// <summary>Steam 方形头图（460×215，宽扁），适合卡片位。</summary>
    public static Uri? BuildSteamCoverUri(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
        {
            return null;
        }

        return new Uri(SteamCdnBase, $"{appId}/header.jpg");
    }

    /// <summary>取指定形态的 CDN 地址；非 Steam 游戏没有 CDN 主视觉，返回 null。</summary>
    public static Uri? BuildUri(Game game, PosterKind kind)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Source != GameSource.Steam)
        {
            return null;
        }

        return kind switch
        {
            PosterKind.Portrait => BuildSteamPortraitUri(game.ExternalId),
            PosterKind.Square => BuildSteamCoverUri(game.ExternalId),
            _ => null,
        };
    }

    /// <summary>该游戏是否支持这个形态（非 Steam 游戏没有 CDN 主视觉，不支持任何形态）。</summary>
    public static bool SupportsKind(Game game, PosterKind kind)
    {
        ArgumentNullException.ThrowIfNull(game);
        return BuildUri(game, kind) is not null;
    }

    /// <summary>已缓存的指定形态路径；没缓存过返回 null。</summary>
    public string? GetCachedPath(Game game, PosterKind kind)
    {
        ArgumentNullException.ThrowIfNull(game);
        var path = BuildCachePath(game, kind);
        return path is not null && _fileSystem.FileExists(path) ? path : null;
    }

    /// <summary>
    /// 旧版缓存（升级前下载的、不分形态的 <c>steam-&lt;appId&gt;.jpg</c>）路径。
    /// </summary>
    public string? GetLegacyCachedPath(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var path = BuildLegacyCachePath(game);
        return path is not null && _fileSystem.FileExists(path) ? path : null;
    }

    /// <summary>
    /// 判断旧缓存属于哪种形态：按图片实际宽高判定（高 &gt; 宽 = 竖屏长图）。
    /// 读不出尺寸时按竖屏处理（旧版优先下载的就是竖版）。
    /// </summary>
    public PosterKind ClassifyLegacyCached(Game game)
    {
        var path = GetLegacyCachedPath(game);
        if (path is null)
        {
            return PosterKind.Portrait;
        }

        try
        {
            using var stream = _fileSystem.OpenRead(path);
            var size = JpegSizeReader.Read(stream);
            if (size is null)
            {
                return PosterKind.Portrait;
            }

            return size.Value.Height > size.Value.Width ? PosterKind.Portrait : PosterKind.Square;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug($"读取旧海报尺寸失败（按竖屏处理）：{game.Name} / {ex.Message}");
            return PosterKind.Portrait;
        }
    }

    /// <summary>该游戏当前缓存了哪些形态（按 <see cref="PosterKinds.All"/> 的顺序）。</summary>
    public IReadOnlyList<PosterKind> GetCachedKinds(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        var result = new List<PosterKind>();
        foreach (var kind in PosterKinds.All)
        {
            if (GetCachedPath(game, kind) is not null)
            {
                result.Add(kind);
            }
        }

        return result;
    }

    /// <summary>
    /// 取指定形态的本地路径：命中缓存直接返回，否则尝试下载。
    /// 下载失败返回 null（UI 退回首字母色块），绝不影响备份主流程。
    /// </summary>
    public async Task<string?> GetAsync(Game game, PosterKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);

        var cached = GetCachedPath(game, kind);
        if (cached is not null)
        {
            return cached;
        }

        if (_downloader is null)
        {
            return null;
        }

        var uri = BuildUri(game, kind);
        if (uri is null)
        {
            return null;
        }

        return await TryDownloadAsync(game, kind, uri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 依次尝试所有形态，返回每种形态的本地路径（拿不到的是 null）。
    /// 供界面一次性准备齐可切换的海报，避免切换时才现下载。
    /// </summary>
    public async Task<IReadOnlyDictionary<PosterKind, string?>> GetAllAsync(Game game, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);

        var result = new Dictionary<PosterKind, string?>();
        foreach (var kind in PosterKinds.All)
        {
            result[kind] = await GetAsync(game, kind, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<string?> TryDownloadAsync(Game game, PosterKind kind, Uri uri, CancellationToken ct)
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

            var target = BuildCachePath(game, kind);
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
            _logger.Debug($"海报已缓存：{game.Name}（{kind.DisplayName()}）");
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Debug($"海报下载失败（忽略）：{game.Name} / {kind.DisplayName()} / {ex.Message}");
            return null;
        }
    }

    private string? BuildCachePath(Game game, PosterKind kind)
    {
        var key = BuildCacheKey(game);
        return key is null ? null : Path.Combine(CoversDirectory, $"{key}-{kind.Slug()}.jpg");
    }

    private string? BuildLegacyCachePath(Game game)
    {
        var key = BuildCacheKey(game);
        return key is null ? null : Path.Combine(CoversDirectory, key + ".jpg");
    }

    private static string? BuildCacheKey(Game game)
    {
        var key = game.Source == GameSource.Steam && !string.IsNullOrWhiteSpace(game.ExternalId)
            ? $"steam-{game.ExternalId}"
            : null;

        if (key is null || !key.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        return key;
    }
}
