using System.Net;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Online;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;
using Xunit;

namespace GameSaveSync.Tests;

/// <summary>
/// Ludusavi Manifest 存档路径数据源的测试：
/// 索引构建、块查找、路径占位符转换、多源回退。
/// </summary>
public sealed class M8ManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "reload-m8-" + Guid.NewGuid().ToString("N"));

    private static string SampleManifest => """
        ---
        "!Weird: Game!":
          steam:
            id: 111
        "C":
          steam:
            id: 145
        "Hollow Knight":
          files:
            "<winLocalAppDataLow>/Team Cherry/Hollow Knight":
              tags:
                - save
              when:
                - os: windows
            "<winAppData>/Hollow Knight/Config":
              tags:
                - config
              when:
                - os: windows
            "<xdgData>/hollow_knight":
              when:
                - os: linux
          steam:
            id: 367520
        Watch Dogs:
          files:
            "<winDocuments>/My Games/Watch_Dogs/<storeUserId>/GamerProfile.xml":
              tags:
                - config
              when:
                - os: windows
            "<root>/savegames/<storeUserId>/274":
              tags:
                - save
              when:
                - os: windows
                  store: uplay
          id:
            lutris: watch-dogs
          steam:
            id: 223100
        Lossless Scaling:
          files:
            "<winLocalAppData>/Lossless Scaling":
              tags:
                - save
          steam:
            id: 993090
        """;

    private AppPaths NewPaths()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"));
        paths.EnsureCreated();
        return paths;
    }

    private static IFileSystem NewFs() => new PhysicalFileSystem();

    // ── 索引 ────────────────────────────────────────────────

    [Fact]
    public void Index_BuildsBlocksWithAndWithoutQuotes()
    {
        var index = ManifestIndex.Build(SampleManifest);

        Assert.Equal(5, index.Blocks.Count);
        Assert.Contains(index.Blocks, b => b.Name == "!Weird: Game!");
        Assert.Contains(index.Blocks, b => b.Name == "Hollow Knight");
        Assert.Contains(index.Blocks, b => b.Name == "Watch Dogs");
        Assert.Contains(index.Blocks, b => b.Name == "Lossless Scaling");
    }

    [Fact]
    public void Index_MapsSteamAppIdToBlock()
    {
        var index = ManifestIndex.Build(SampleManifest);

        var block = index.FindBlock("任意名字", "367520");

        Assert.NotNull(block);
        Assert.Equal("Hollow Knight", block!.Name);
    }

    [Fact]
    public void Index_FindsByNormalizedName()
    {
        var index = ManifestIndex.Build(SampleManifest);

        // 大小写、空格、标点差异都应命中
        Assert.Equal("Watch Dogs", index.FindBlock("watch  dogs", null)?.Name);
        Assert.Equal("Hollow Knight", index.FindBlock("HOLLOWKNIGHT", null)?.Name);
    }

    [Fact]
    public void Index_PartialNameMatchPicksShortestCandidate()
    {
        var index = ManifestIndex.Build(SampleManifest);

        // "Watch Dogs" 应该命中 Watch Dogs 而不是别的
        Assert.Equal("Watch Dogs", index.FindBlock("Watch", null)?.Name);
    }

    [Fact]
    public void Index_ShortGameNameDoesNotMatchUnrelatedGame()
    {
        // 清单里有一堆单字母游戏名（"C"）。如果名称匹配用"任意包含"，
        // 查 "Lossless Scaling" 会被 "c" 命中（名字里带 c），完全不相干。
        var index = ManifestIndex.Build(SampleManifest);

        // "Abcdef Game" 规范化后含 "c"，但和任何条目都不是前缀关系
        Assert.Null(index.FindBlock("Abcdef Game", null));
        Assert.Equal("C", index.FindBlock("C", null)?.Name);
    }

    [Fact]
    public void Index_UnknownGame_ReturnsNull()
    {
        var index = ManifestIndex.Build(SampleManifest);

        Assert.Null(index.FindBlock("完全不相干的游戏名字", null));
        Assert.Null(index.FindBlock("完全不相干的游戏名字", "999999"));
    }

    [Fact]
    public void Index_BlockTextContainsOnlyItsOwnLines()
    {
        var index = ManifestIndex.Build(SampleManifest);
        var block = index.FindBlock("Hollow Knight", null);

        Assert.NotNull(block);
        var text = index.GetBlockText(block!);

        Assert.Contains("Team Cherry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lossless Scaling", text, StringComparison.Ordinal);
    }

    // ── 占位符转换 ───────────────────────────────────────────

    [Theory]
    [InlineData("<winLocalAppData>/Foo", "%LOCALAPPDATA%\\Foo")]
    [InlineData("<winLocalAppDataLow>/Team Cherry/X", "%LOCALAPPDATA%\\Low\\Team Cherry\\X")]
    [InlineData("<winAppData>/Foo", "%APPDATA%\\Foo")]
    [InlineData("<winDocuments>/My Games/X", "%DOCUMENTS%\\My Games\\X")]
    [InlineData("<home>/X", "%USERPROFILE%\\X")]
    public void ToToken_ReplacesPlaceholders(string input, string expected)
    {
        Assert.Equal(expected, LudusaviManifestParser.ToToken(input));
    }

    [Fact]
    public void ToToken_RemovesStoreUserIdSegment()
    {
        var result = LudusaviManifestParser.ToToken("<winDocuments>/My Games/G/<storeUserId>/save.dat");

        Assert.Equal("%DOCUMENTS%\\My Games\\G", result);
    }

    [Fact]
    public void ToToken_FilePathBecomesParentDirectory()
    {
        var result = LudusaviManifestParser.ToToken("%LOCALAPPDATA%\\Game\\config.ini");

        Assert.Equal("%LOCALAPPDATA%\\Game", result);
    }

    [Fact]
    public void ToToken_SkipsUnresolvablePaths()
    {
        // <root> 依赖用户自己配的存档根，<store> 是商店类型，都无法静态定位
        Assert.Null(LudusaviManifestParser.ToToken("<root>/savegames/274"));
        Assert.Null(LudusaviManifestParser.ToToken("<store>/something"));
        Assert.Null(LudusaviManifestParser.ToToken(""));
        Assert.Null(LudusaviManifestParser.ToToken(null!));
    }

    [Fact]
    public void ToToken_WildcardPathTruncatesToDirectory()
    {
        var result = LudusaviManifestParser.ToToken("%LOCALAPPDATA%\\Game\\slot*.dat");

        Assert.Equal("%LOCALAPPDATA%\\Game", result);
    }

    // ── 路径提取 ────────────────────────────────────────────

    [Fact]
    public void Extract_SplitsSavesAndConfig()
    {
        var paths = LudusaviManifestParser.ExtractWindowsPaths(
            SampleManifest,
            "Hollow Knight",
            "https://example.test");

        Assert.Contains(paths, p => p.Kind == OnlineSavePathKind.Saves &&
                                    p.PathTemplate == "%LOCALAPPDATA%\\Low\\Team Cherry\\Hollow Knight");
        Assert.Contains(paths, p => p.Kind == OnlineSavePathKind.Config &&
                                    p.PathTemplate == "%APPDATA%\\Hollow Knight\\Config");
    }

    [Fact]
    public void Extract_SkipsNonWindowsEntries()
    {
        var paths = LudusaviManifestParser.ExtractWindowsPaths(
            SampleManifest,
            "Hollow Knight",
            "https://example.test");

        Assert.DoesNotContain(paths, p => p.PathTemplate.Contains("xdgData", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_SkipsRootDependentPaths()
    {
        var paths = LudusaviManifestParser.ExtractWindowsPaths(
            SampleManifest,
            "Watch Dogs",
            "https://example.test");

        Assert.DoesNotContain(paths, p => p.PathTemplate.Contains("<root>", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_UsesFileParentDirectoryAsRoot()
    {
        var paths = LudusaviManifestParser.ExtractWindowsPaths(
            SampleManifest,
            "Watch Dogs",
            "https://example.test");

        Assert.Contains(paths, p => p.PathTemplate == "%DOCUMENTS%\\My Games\\Watch_Dogs");
    }

    // ── 多源回退 ────────────────────────────────────────────

    [Fact]
    public async Task Refresh_FallsBackToNextSourceWhenFirstFails()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var failing = new FailingProvider("403 Forbidden");
            var working = new WorkingProvider(new[]
            {
                new OnlineSavePath("G", "%LOCALAPPDATA%\\G", OnlineSavePathKind.Saves, "https://ok"),
            });

            var service = new SavePathRefreshService(new IOnlineSavePathProvider[] { failing, working }, env.Tokens);

            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
            Assert.Equal(1, failing.Calls);
            Assert.Equal(1, working.Calls);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_FallsBackWhenSourceHasNoRecord()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var empty = new WorkingProvider(Array.Empty<OnlineSavePath>());
            var working = new WorkingProvider(new[]
            {
                new OnlineSavePath("G", "%LOCALAPPDATA%\\G", OnlineSavePathKind.Saves, "https://ok"),
            });

            var service = new SavePathRefreshService(new IOnlineSavePathProvider[] { empty, working }, env.Tokens);

            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_AllSourcesFail_KeepsOriginalAndReportsAllReasons()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var service = new SavePathRefreshService(
                new IOnlineSavePathProvider[] { new FailingProvider("清单下载失败"), new FailingProvider("403") },
                env.Tokens);

            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Roots);
            Assert.Contains("清单下载失败", result.Message, StringComparison.Ordinal);
            Assert.Contains("403", result.Message, StringComparison.Ordinal);
            Assert.Contains("保留原有配置", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_PassesExternalIdToProvider()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new WorkingProvider(Array.Empty<OnlineSavePath>());
            var service = new SavePathRefreshService(new IOnlineSavePathProvider[] { provider }, env.Tokens);

            await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None, "367520");

            Assert.Equal("367520", provider.LastExternalId);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    // ── Provider 端到端（假 HTTP）────────────────────────────

    [Fact]
    public async Task Provider_DownloadsCachesAndQueriesByAppId()
    {
        var paths = NewPaths();
        var handler = new FakeHandler(SampleManifest);
        using var client = new HttpClient(handler);
        var logger = new InMemoryAppLogger();

        using var provider = new LudusaviManifestProvider(paths, NewFs(), logger, client: client);
        var result = await provider.QueryDetailedAsync("随便什么名字", "367520", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Paths, p => p.PathTemplate.Contains("Team Cherry", StringComparison.Ordinal));
        Assert.True(File.Exists(provider.ManifestFilePath));
        Assert.Equal(1, handler.Downloads);
    }

    [Fact]
    public async Task Provider_SecondQueryUsesCacheWithoutRedownloading()
    {
        var paths = NewPaths();
        var handler = new FakeHandler(SampleManifest);
        using var client = new HttpClient(handler);
        var logger = new InMemoryAppLogger();

        using var provider = new LudusaviManifestProvider(paths, NewFs(), logger, client: client);

        await provider.QueryDetailedAsync("Hollow Knight", null, CancellationToken.None);
        var second = await provider.QueryDetailedAsync("Watch Dogs", null, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(1, handler.Downloads);
    }

    [Fact]
    public async Task Provider_UnknownGame_ReturnsEmptyButSucceeded()
    {
        var paths = NewPaths();
        var handler = new FakeHandler(SampleManifest);
        using var client = new HttpClient(handler);
        var logger = new InMemoryAppLogger();

        using var provider = new LudusaviManifestProvider(paths, NewFs(), logger, client: client);
        var result = await provider.QueryDetailedAsync("根本没有这个游戏", null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Paths);
    }

    // ── 替身 ────────────────────────────────────────────────

    private sealed class FailingProvider : IOnlineSavePathProvider
    {
        private readonly string _error;

        public FailingProvider(string error) => _error = error;

        public int Calls { get; private set; }

        public Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OnlineSavePath>>(Array.Empty<OnlineSavePath>());

        public Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(OnlineSavePathQueryResult.Fail(_error));
        }
    }

    private sealed class WorkingProvider : IOnlineSavePathProvider
    {
        private readonly IReadOnlyList<OnlineSavePath> _paths;

        public WorkingProvider(IReadOnlyList<OnlineSavePath> paths) => _paths = paths;

        public int Calls { get; private set; }

        public string? LastExternalId { get; private set; }

        public Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct) =>
            Task.FromResult(_paths);

        public Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(OnlineSavePathQueryResult.Ok(_paths));
        }

        public Task<OnlineSavePathQueryResult> QueryDetailedAsync(
            string gameName,
            string? externalId,
            CancellationToken ct)
        {
            LastExternalId = externalId;
            return QueryDetailedAsync(gameName, ct);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _content;

        public FakeHandler(string content) => _content = content;

        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Downloads++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_content),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 忽略清理失败
        }

        GC.SuppressFinalize(this);
    }
}
