using System.Net;
using System.Text;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Online;

namespace GameSaveSync.Tests;

/// <summary>M4.5 在线功能：PCGamingWiki 存档路径解析/查询、竖版海报 URI。</summary>
public class MOnlineTests
{
    // ===== 解析器 =====

    [Fact]
    public void Parser_ExpandsSavesTemplateToTokenPath()
    {
        const string wikitext = """
            {{Infobox game}}
            {{Game data/saves|Windows|{{p|userprofile\Saved Games\CD Projekt Red\Cyberpunk 2077}}}}
            """;

        var result = PcGamingWikiParser.Parse(wikitext, "Cyberpunk 2077", "https://example/wiki");

        Assert.Single(result);
        Assert.Equal("%USERPROFILE%\\Saved Games\\CD Projekt Red\\Cyberpunk 2077", result[0].PathTemplate);
        Assert.Equal(OnlineSavePathKind.Saves, result[0].Kind);
    }

    [Fact]
    public void Parser_ExpandsConfigAndAppdataTokens()
    {
        const string wikitext = """
            {{Game data/config|Windows|{{p|localappdata\Foo\settings.ini}}}}
            """;

        var result = PcGamingWikiParser.Parse(wikitext, "Foo", "u");

        Assert.Single(result);
        Assert.Equal("%LOCALAPPDATA%\\Foo\\settings.ini", result[0].PathTemplate);
        Assert.Equal(OnlineSavePathKind.Config, result[0].Kind);
    }

    [Fact]
    public void Parser_SkipsRegistryPaths()
    {
        const string wikitext = """
            {{Game data/saves|Windows|{{p|hklm\software\foo}}}}
            {{Game data/saves|Windows|{{p|userprofile\Documents\Foo}}}}
            """;

        var result = PcGamingWikiParser.Parse(wikitext, "Foo", "u");

        Assert.Single(result);
        Assert.Equal("%USERPROFILE%\\Documents\\Foo", result[0].PathTemplate);
    }

    [Fact]
    public void Parser_KeepsInstallDirPlaceholder()
    {
        const string wikitext = "{{Game data/saves|Windows|{{p|game\\save}}}}";

        var result = PcGamingWikiParser.Parse(wikitext, "Foo", "u");

        Assert.Single(result);
        Assert.Equal("{installDir}\\save", result[0].PathTemplate);
    }

    [Fact]
    public void Parser_Deduplicates()
    {
        const string wikitext = """
            {{Game data/saves|Windows|{{p|userprofile\Saved Games\Foo}}}}
            {{Game data/saves|Windows|{{p|userprofile\Saved Games\Foo}}}}
            """;

        var result = PcGamingWikiParser.Parse(wikitext, "Foo", "u");

        Assert.Single(result);
    }

    // ===== Provider（假 HTTP） =====

    [Fact]
    public async Task Provider_QueriesSearchThenWikitext()
    {
        var handler = new FakeHandler(
            (url) => url.Contains("list=search")
                ? """{"query":{"search":[{"title":"Cyberpunk 2077"}]}}"""
                : """{"parse":{"wikitext":{"*":"{{Game data/saves|Windows|{{p|userprofile\\Saved Games\\Foo}}}}"}}}""");

        using var provider = new PcGamingWikiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://test/") });

        var result = await provider.QueryAsync("Cyberpunk 2077", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("%USERPROFILE%\\Saved Games\\Foo", result[0].PathTemplate);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Provider_ReturnsEmptyOnHttpError()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));

        using var provider = new PcGamingWikiProvider(new HttpClient(handler));

        var result = await provider.QueryAsync("Foo", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Provider_ReturnsEmptyOnNonJson()
    {
        var handler = new FakeHandler(_ => "<html>Cloudflare blocked</html>");

        using var provider = new PcGamingWikiProvider(new HttpClient(handler));

        var result = await provider.QueryAsync("Foo", CancellationToken.None);

        Assert.Empty(result);
    }

    // ===== 封面 URI =====

    [Fact]
    public void CoverService_BuildsPortraitAndHeaderUris()
    {
        var portrait = CoverService.BuildSteamPortraitUri("1091500");
        Assert.NotNull(portrait);
        Assert.EndsWith("1091500/library_600x900.jpg", portrait!.ToString(), StringComparison.Ordinal);

        var header = CoverService.BuildSteamCoverUri("1091500");
        Assert.EndsWith("1091500/header.jpg", header!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CoverService_RejectsNonNumericAppId()
    {
        Assert.Null(CoverService.BuildSteamPortraitUri("not-a-number"));
        Assert.Null(CoverService.BuildSteamPortraitUri(""));
        Assert.Null(CoverService.BuildSteamPortraitUri(null));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _respond;

        public FakeHandler(Func<string, string> respond) => _respond = respond;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            try
            {
                var body = _respond(request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
