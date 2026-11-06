using System.Text.Json;

namespace GameSaveSync.Core.Online;

/// <summary>
/// PCGamingWiki 存档路径查询（MediaWiki API）。
/// 流程：按名搜索页面 → 取 wikitext → 解析 Game data/saves、Game data/config 模板。
/// 任何失败（无网络、被 Cloudflare 拦、页面不存在）都返回空列表，静默降级。
/// </summary>
public sealed class PcGamingWikiProvider : IOnlineSavePathProvider, IDisposable
{
    private static readonly Uri ApiBase = new("https://www.pcgamingwiki.com/w/api.php");

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Reload/0.4";

    private readonly HttpClient _client;

    public PcGamingWikiProvider(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        _client.DefaultRequestHeaders.UserAgent.TryParseAdd(BrowserUserAgent);
    }

    public async Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return Array.Empty<OnlineSavePath>();
        }

        try
        {
            var title = await SearchAsync(gameName, ct).ConfigureAwait(false);
            if (title is null)
            {
                return Array.Empty<OnlineSavePath>();
            }

            var wikitext = await FetchWikitextAsync(title, ct).ConfigureAwait(false);
            if (wikitext is null)
            {
                return Array.Empty<OnlineSavePath>();
            }

            var sourceUrl = $"https://www.pcgamingwiki.com/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
            return PcGamingWikiParser.Parse(wikitext, gameName, sourceUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 静默降级：外部服务不可用不影响备份
            return Array.Empty<OnlineSavePath>();
        }
    }

    private async Task<string?> SearchAsync(string gameName, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=query&list=search&srsearch={Uri.EscapeDataString(gameName)}&srlimit=1&format=json&formatversion=2";
        using var json = await _client.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("search", out var search) ||
            search.GetArrayLength() == 0)
        {
            return null;
        }

        return search[0].GetProperty("title").GetString();
    }

    private async Task<string?> FetchWikitextAsync(string title, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=parse&page={Uri.EscapeDataString(title)}&prop=wikitext&format=json&formatversion=2&redirects=1";
        using var json = await _client.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("parse", out var parse) ||
            !parse.TryGetProperty("wikitext", out var wikitext))
        {
            return null;
        }

        // formatversion=2：wikitext 直接是字符串；
        // formatversion=1（部分部署/回退）：是 {"*": "..."} 对象。两种都要认。
        if (wikitext.ValueKind == JsonValueKind.String)
        {
            return wikitext.GetString();
        }

        if (wikitext.ValueKind == JsonValueKind.Object &&
            wikitext.TryGetProperty("*", out var text))
        {
            return text.GetString();
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
