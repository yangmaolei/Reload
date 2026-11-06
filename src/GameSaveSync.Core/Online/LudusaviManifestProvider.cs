using System.Net;
using System.Text;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.Core.Online;

/// <summary>
/// 从 <b>Ludusavi Manifest</b> 获取已知存档路径。
/// <para>
/// 数据源：<c>https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml</c>
/// </para>
/// <para>
/// 为什么用它：PCGamingWiki 的网页与 API 在很多网络环境下会被 Cloudflare 拦截
/// （匿名请求返回 403），而这个清单是同一批数据的公开编译产物，托管在 GitHub Raw 上，
/// 不走 Cloudflare 挑战页，可以稳定下载。它同时提供 <c>steam: id:</c> 字段，
/// 能用 Steam AppID 精确命中，避免同名游戏匹配错。
/// </para>
/// <para>
/// 清单本身约 17 MB，首次使用需要下载一次；之后按 ETag 增量更新（未变更时服务端返回 304，
/// 直接复用本地缓存）。清单缓存在数据目录下，不进版本库也不参与云同步。
/// </para>
/// </summary>
public sealed class LudusaviManifestProvider : IOnlineSavePathProvider, IDisposable
{
    /// <summary>清单默认下载地址。</summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    /// <summary>清单里 Steam AppID 字段的说明链接（用于在界面上给用户看来源）。</summary>
    public const string SourceUrl = "https://github.com/mtkennerly/ludusavi-manifest";

    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ManifestIndex? _index;

    public LudusaviManifestProvider(
        AppPaths paths,
        IFileSystem fileSystem,
        IAppLogger logger,
        IWebProxyProvider? proxyProvider = null,
        HttpClient? client = null,
        string manifestUrl = DefaultManifestUrl)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ManifestUrl = manifestUrl;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        // 显式指定系统代理：环境变量里的代理往往只覆盖部分域名，
        // 用它会把本来能直连的公开数据源也绕过去。
        var proxy = proxyProvider?.GetProxy();
        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        _client = client ?? new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        _client.DefaultRequestHeaders.UserAgent.TryParseAdd("Reload/1.0 (+https://github.com/yangmaolei/Reload)");
    }

    /// <summary>清单下载地址。</summary>
    public string ManifestUrl { get; }

    /// <summary>本地缓存的清单文件路径。</summary>
    public string ManifestFilePath => Path.Combine(_paths.DataRoot, "manifest", "ludusavi-manifest.yaml");

    /// <summary>本地缓存的 ETag（用于增量更新）。</summary>
    public string ETagFilePath => Path.Combine(_paths.DataRoot, "manifest", "ludusavi-manifest.etag");

    public async Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct)
    {
        var result = await QueryDetailedAsync(gameName, null, ct).ConfigureAwait(false);
        return result.Succeeded ? result.Paths : Array.Empty<OnlineSavePath>();
    }

    public Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct) =>
        QueryDetailedAsync(gameName, null, ct);

    /// <summary>
    /// 带 Steam AppID 的查询。AppID 命中时用精确匹配（最可靠），
    /// 否则退回按游戏名匹配。
    /// </summary>
    public async Task<OnlineSavePathQueryResult> QueryDetailedAsync(
        string gameName,
        string? externalId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return OnlineSavePathQueryResult.Fail("游戏名称为空。");
        }

        ManifestIndex index;
        try
        {
            index = await EnsureIndexAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("存档路径清单下载失败", ex);
            return OnlineSavePathQueryResult.Fail($"存档路径清单不可用：{ex.Message}");
        }

        var block = index.FindBlock(gameName, externalId);
        if (block is null)
        {
            return OnlineSavePathQueryResult.Ok(Array.Empty<OnlineSavePath>(), SourceUrl);
        }

        var source = $"{SourceUrl}#{Uri.EscapeDataString(block.Name)}";
        var paths = LudusaviManifestParser.ExtractWindowsPaths(index.GetBlockText(block), gameName, source);
        return OnlineSavePathQueryResult.Ok(paths, source);
    }

    /// <summary>
    /// 确保本地有可用的清单索引：优先用本地缓存（带 ETag 校验，未变更则不重新下载）。
    /// </summary>
    private async Task<ManifestIndex> EnsureIndexAsync(CancellationToken ct)
    {
        if (_index is not null)
        {
            return _index;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is not null)
            {
                return _index;
            }

            var text = await LoadManifestTextAsync(ct).ConfigureAwait(false);
            _index = ManifestIndex.Build(text);
            _logger.Info($"存档路径清单索引就绪：{_index.Blocks.Count} 个游戏条目");
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>读取清单文本：本地没有就下载；本地有则用 ETag 校验，未变更就不重新下载。</summary>
    private async Task<string> LoadManifestTextAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(ManifestFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var etag = ReadEtag();
        var hasLocal = File.Exists(ManifestFilePath);

        if (hasLocal)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
                if (!string.IsNullOrEmpty(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    _logger.Info("存档路径清单未变更，使用本地缓存");
                    return await File.ReadAllTextAsync(ManifestFilePath, ct).ConfigureAwait(false);
                }

                response.EnsureSuccessStatusCode();
                var fresh = await ReadAllAsync(response, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(ManifestFilePath, fresh, ct).ConfigureAwait(false);
                WriteEtag(response.Headers.ETag?.Tag);
                _logger.Info($"存档路径清单已更新：{fresh.Length} 字符");
                return fresh;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 校验失败不影响使用本地缓存
                _logger.Warn("存档路径清单增量更新失败，改用本地缓存", ex);
                return await File.ReadAllTextAsync(ManifestFilePath, ct).ConfigureAwait(false);
            }
        }

        // 本地没有：必须下载
        using var download = await _client
            .GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        download.EnsureSuccessStatusCode();
        var content = await ReadAllAsync(download, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(ManifestFilePath, content, ct).ConfigureAwait(false);
        WriteEtag(download.Headers.ETag?.Tag);
        _logger.Info($"存档路径清单已下载：{content.Length} 字符");
        return content;
    }

    private static async Task<string> ReadAllAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private string? ReadEtag()
    {
        try
        {
            return File.Exists(ETagFilePath) ? File.ReadAllText(ETagFilePath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteEtag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag))
        {
            return;
        }

        try
        {
            File.WriteAllText(ETagFilePath, etag);
        }
        catch (Exception ex)
        {
            _logger.Warn("写入清单 ETag 失败", ex);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}
