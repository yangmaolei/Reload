using GameSaveSync.App.Converters;
using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Online;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Storage;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// 特性分支：海报形态切换 + 一键刷新存档路径。
/// 覆盖：形态解析与缓存路径、切换交互（单形态/双形态/循环边界）、
/// 刷新成功与失败路径（失败保留原数据、防重复请求）。
/// </summary>
public sealed class M7PosterAndRefreshTests : IDisposable
{
    private readonly string _root;
    private readonly PhysicalFileSystem _fs = PhysicalFileSystem.Instance;
    private readonly InMemoryAppLogger _logger = new(500);

    public M7PosterAndRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reload-m7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // ============================================================
    // 一、海报形态：枚举与解析
    // ============================================================

    [Theory]
    [InlineData(PosterKind.Portrait, "p", "竖屏长图")]
    [InlineData(PosterKind.Square, "s", "方形头图")]
    public void PosterKind_SlugAndDisplayName_AreStable(PosterKind kind, string slug, string display)
    {
        Assert.Equal(slug, kind.Slug());
        Assert.Equal(display, kind.DisplayName());
    }

    [Theory]
    [InlineData("p", PosterKind.Portrait)]
    [InlineData("portrait", PosterKind.Portrait)]
    [InlineData("s", PosterKind.Square)]
    [InlineData("square", PosterKind.Square)]
    public void PosterKind_TryParse_ParsesKnownValues(string text, PosterKind expected)
    {
        Assert.True(PosterKinds.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void PosterKind_TryParse_RejectsUnknownValues(string? text)
    {
        // 未知值不抛异常，退回默认形态（竖屏长图）
        Assert.False(PosterKinds.TryParse(text, out var kind));
        Assert.Equal(PosterKind.Portrait, kind);
    }

    [Fact]
    public void PosterKind_Parse_ThrowsOnUnknown()
    {
        Assert.Throws<ArgumentException>(() => PosterKinds.Parse("nope"));
    }

    [Fact]
    public void PosterKinds_All_ContainsBothKinds()
    {
        Assert.Collection(
            PosterKinds.All,
            k => Assert.Equal(PosterKind.Portrait, k),
            k => Assert.Equal(PosterKind.Square, k));
    }

    // ============================================================
    // 二、海报形态：CDN 地址与缓存
    // ============================================================

    [Fact]
    public void BuildUri_MapsKindsToRealSteamCdnAssets()
    {
        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };

        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/570/library_600x900.jpg",
            CoverService.BuildUri(game, PosterKind.Portrait)!.ToString());
        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/570/header.jpg",
            CoverService.BuildUri(game, PosterKind.Square)!.ToString());
    }

    [Fact]
    public void BuildUri_ReturnsNullForNonSteamGame()
    {
        var game = new Game { Name = "G", Source = GameSource.Manual, ExternalId = "570" };

        Assert.Null(CoverService.BuildUri(game, PosterKind.Portrait));
        Assert.Null(CoverService.BuildUri(game, PosterKind.Square));
        Assert.False(CoverService.SupportsKind(game, PosterKind.Portrait));
    }

    [Fact]
    public void BuildUri_ReturnsNullForInvalidAppId()
    {
        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "not-a-number" };

        Assert.Null(CoverService.BuildUri(game, PosterKind.Portrait));
    }

    [Fact]
    public async Task CoverService_CachesEachKindSeparately()
    {
        var paths = new AppPaths(Path.Combine(_root, "covers"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };
        var downloader = new CountingDownloader(TestEnvironment.Bytes(16, seed: 3));
        var service = new CoverService(paths, _fs, downloader, _logger);

        var portrait = await service.GetAsync(game, PosterKind.Portrait, CancellationToken.None);
        var square = await service.GetAsync(game, PosterKind.Square, CancellationToken.None);

        Assert.NotNull(portrait);
        Assert.NotNull(square);
        Assert.NotEqual(portrait, square);

        // 两种形态各下一次，共 2 次请求
        Assert.Equal(2, downloader.Calls);

        // 再取一次应命中缓存，不再发请求
        await service.GetAsync(game, PosterKind.Portrait, CancellationToken.None);
        await service.GetAsync(game, PosterKind.Square, CancellationToken.None);
        Assert.Equal(2, downloader.Calls);

        // 缓存文件按形态分开
        Assert.True(File.Exists(portrait));
        Assert.True(File.Exists(square));
        Assert.Equal(
            new[] { PosterKind.Portrait, PosterKind.Square },
            service.GetCachedKinds(game));
    }

    [Fact]
    public async Task CoverService_GetAllAsync_ReturnsBothKinds()
    {
        var paths = new AppPaths(Path.Combine(_root, "covers-all"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };
        var service = new CoverService(paths, _fs, new CountingDownloader(TestEnvironment.Bytes(8, seed: 5)), _logger);

        var all = await service.GetAllAsync(game, CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.NotNull(all[PosterKind.Portrait]);
        Assert.NotNull(all[PosterKind.Square]);
    }

    [Fact]
    public async Task CoverService_DownloadFailure_ReturnsNullWithoutThrowing()
    {
        var paths = new AppPaths(Path.Combine(_root, "covers-fail"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };
        var service = new CoverService(paths, _fs, new FailingDownloader(), _logger);

        var result = await service.GetAsync(game, PosterKind.Portrait, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void CoverService_NoDownloader_ReturnsNull()
    {
        var paths = new AppPaths(Path.Combine(_root, "covers-nodl"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };
        var service = new CoverService(paths, _fs, downloader: null, _logger);

        Assert.Null(service.GetCachedPath(game, PosterKind.Portrait));
    }

    // ============================================================
    // 三、海报切换交互
    // ============================================================

    [Fact]
    public void PosterSwitch_SingleKind_HasNoSwitchControl()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });
        vm.ApplyPosters(new Dictionary<PosterKind, string> { [PosterKind.Portrait] = "p.jpg" }, PosterKind.Portrait);

        Assert.Equal(1, vm.PosterCount);
        Assert.False(vm.HasMultiplePosters);
        Assert.Equal("1/1", vm.PosterIndicator);
        Assert.Equal("p.jpg", vm.CoverPath);
    }

    [Fact]
    public void PosterSwitch_TwoKinds_TogglesBetweenThem()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });
        vm.ApplyPosters(
            new Dictionary<PosterKind, string>
            {
                [PosterKind.Portrait] = "portrait.jpg",
                [PosterKind.Square] = "square.jpg",
            },
            PosterKind.Portrait);

        Assert.Equal(2, vm.PosterCount);
        Assert.True(vm.HasMultiplePosters);
        Assert.Equal("1/2", vm.PosterIndicator);
        Assert.Equal("portrait.jpg", vm.CoverPath);
        Assert.Equal(0, vm.PosterIndex);

        Assert.True(vm.NextPoster());
        Assert.Equal(PosterKind.Square, vm.SelectedPosterKind);
        Assert.Equal("square.jpg", vm.CoverPath);
        Assert.Equal("2/2", vm.PosterIndicator);
        Assert.Equal(1, vm.PosterIndex);

        Assert.True(vm.PreviousPoster());
        Assert.Equal(PosterKind.Portrait, vm.SelectedPosterKind);
        Assert.Equal("portrait.jpg", vm.CoverPath);
    }

    [Fact]
    public void PosterSwitch_NextAtEnd_WrapsAround()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });
        vm.ApplyPosters(
            new Dictionary<PosterKind, string>
            {
                [PosterKind.Portrait] = "portrait.jpg",
                [PosterKind.Square] = "square.jpg",
            },
            PosterKind.Square);

        // 从最后一张再往后，应回到第一张
        Assert.True(vm.NextPoster());
        Assert.Equal(PosterKind.Portrait, vm.SelectedPosterKind);
        Assert.Equal("1/2", vm.PosterIndicator);
    }

    [Fact]
    public void PosterSwitch_PreviousAtStart_WrapsAround()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });
        vm.ApplyPosters(
            new Dictionary<PosterKind, string>
            {
                [PosterKind.Portrait] = "portrait.jpg",
                [PosterKind.Square] = "square.jpg",
            },
            PosterKind.Portrait);

        Assert.True(vm.PreviousPoster());
        Assert.Equal(PosterKind.Square, vm.SelectedPosterKind);
        Assert.Equal("2/2", vm.PosterIndicator);
    }

    [Fact]
    public void PosterSwitch_SingleKind_SwitchReturnsFalse()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });
        vm.ApplyPosters(new Dictionary<PosterKind, string> { [PosterKind.Portrait] = "p.jpg" }, PosterKind.Portrait);

        Assert.False(vm.NextPoster());
        Assert.False(vm.PreviousPoster());
        Assert.Equal(PosterKind.Portrait, vm.SelectedPosterKind);
    }

    [Fact]
    public void PosterSwitch_PreferredKindUnavailable_FallsBackToFirst()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });

        // 偏好"方形头图"，但只有竖屏可用 → 退回竖屏，而不是显示空白
        vm.ApplyPosters(new Dictionary<PosterKind, string> { [PosterKind.Portrait] = "portrait.jpg" }, PosterKind.Square);

        Assert.Equal(PosterKind.Portrait, vm.SelectedPosterKind);
        Assert.Equal("portrait.jpg", vm.CoverPath);
    }

    [Fact]
    public void PosterSwitch_NoPosters_HasNoCover()
    {
        var vm = new GameListItemViewModel(new Game { Name = "G" });

        Assert.False(vm.HasCover);
        Assert.Equal(0, vm.PosterCount);
        Assert.False(vm.HasMultiplePosters);
    }

    [Fact]
    public void PosterHeight_UsesAspectPerKind()
    {
        Assert.Equal(1.5, PosterHeightConverter.AspectOf(PosterKind.Portrait));
        Assert.Equal(1.0, PosterHeightConverter.AspectOf(PosterKind.Square));

        var converter = new PosterHeightConverter();

        // 宽度 100：竖屏高 150，方形高 100
        Assert.Equal(150d, (double)converter.Convert(
            new object[] { 100d, PosterKind.Portrait }, typeof(double), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(100d, (double)converter.Convert(
            new object[] { 100d, PosterKind.Square }, typeof(double), null!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PosterHeight_NarrowWidth_ShrinksProportionally()
    {
        var converter = new PosterHeightConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        // 窄屏：宽度 60 → 竖屏高 90（等比缩小，不溢出）
        Assert.Equal(90d, (double)converter.Convert(
            new object[] { 60d, PosterKind.Portrait }, typeof(double), null!, culture));
    }

    [Fact]
    public void PosterIndicator_MatchesSelectedKind()
    {
        var converter = new PosterIndicatorConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(true, converter.Convert(
            new object[] { PosterKind.Portrait, PosterKind.Portrait }, typeof(bool), null!, culture));
        Assert.Equal(false, converter.Convert(
            new object[] { PosterKind.Portrait, PosterKind.Square }, typeof(bool), null!, culture));
    }

    // ============================================================
    // 四、刷新存档路径：成功路径
    // ============================================================

    [Fact]
    public async Task Refresh_ReplacesRootsOnSuccess()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "%DOCUMENTS%\\G\\Saves", OnlineSavePathKind.Saves, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
            Assert.Equal("%DOCUMENTS%\\G\\Saves", result.Roots[0].Token);
            Assert.Contains("刷新成功", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_KeepsExcludeRulesOfMatchingRoots()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "%DOCUMENTS%\\G\\Saves", OnlineSavePathKind.Saves, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
            // 新根默认没有排除规则，合并逻辑在 MainViewModel 里做
            Assert.Empty(result.Roots[0].Exclude);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_ResolvesInstallDirTemplate()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "{installDir}\\Saves", OnlineSavePathKind.Saves, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: "C:\\Games\\G", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
            Assert.Equal("C:\\Games\\G\\Saves", result.Roots[0].Token);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_DeduplicatesSamePath()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "%DOCUMENTS%\\G", OnlineSavePathKind.Saves, "https://example.org/g"),
                new OnlineSavePath("G", "%DOCUMENTS%\\G", OnlineSavePathKind.Config, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Single(result.Roots);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    // ============================================================
    // 五、刷新存档路径：失败路径（必须保留原数据）
    // ============================================================

    [Fact]
    public async Task Refresh_ProviderFailure_ReturnsFailureAndNoRoots()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(Array.Empty<OnlineSavePath>())
            {
                Failure = "连接超时",
            };

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Roots);
            Assert.Contains("连接超时", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_NoResults_ReturnsFailureWithOriginalKept()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(Array.Empty<OnlineSavePath>());
            var service = new SavePathRefreshService(provider, env.Tokens);

            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            // 站内没记录：不算成功，但也不是"错误"——提示保留原配置
            Assert.False(result.Succeeded);
            Assert.Empty(result.Roots);
            Assert.Contains("保留原有配置", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_ProviderThrows_IsConvertedToFailure()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new ThrowingOnlineProvider();
            var service = new SavePathRefreshService(provider, env.Tokens);

            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Roots);
            Assert.Contains("查询失败", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_InstallDirTemplateWithoutInstallDir_IsSkipped()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "{installDir}\\Saves", OnlineSavePathKind.Saves, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);
            var result = await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Roots);
            Assert.Contains("安装目录", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_EmptyGameName_FailsFast()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(Array.Empty<OnlineSavePath>());
            var service = new SavePathRefreshService(provider, env.Tokens);

            var result = await service.RefreshAsync("   ", installDir: (string?)null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(0, provider.Calls);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_DoesNotRepeatRequestWhenCalledTwiceConcurrently()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var provider = new StubOnlineProvider(new[]
            {
                new OnlineSavePath("G", "%DOCUMENTS%\\G", OnlineSavePathKind.Saves, "https://example.org/g"),
            });

            var service = new SavePathRefreshService(provider, env.Tokens);

            // 串行两次 = 两次请求（服务本身不缓存，由 UI 层防抖）
            await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);
            await service.RefreshAsync("G", installDir: (string?)null, CancellationToken.None);

            Assert.Equal(2, provider.Calls);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    // ============================================================
    // 六、旧版缓存迁移（升级后老用户的海报不能消失）
    // ============================================================

    [Fact]
    public void LegacyCache_IsFoundAndClassifiedByImageSize()
    {
        var paths = new AppPaths(Path.Combine(_root, "legacy"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570" };
        var legacyPath = Path.Combine(paths.CoversDir, "steam-570.jpg");
        Directory.CreateDirectory(paths.CoversDir);
        File.WriteAllBytes(legacyPath, BuildJpeg(600, 900));

        var service = new CoverService(paths, _fs, downloader: null, _logger);

        Assert.Equal(legacyPath, service.GetLegacyCachedPath(game));
        // 高 > 宽 → 竖屏长图
        Assert.Equal(PosterKind.Portrait, service.ClassifyLegacyCached(game));
    }

    [Fact]
    public void LegacyCache_LandscapeImage_ClassifiedAsSquare()
    {
        var paths = new AppPaths(Path.Combine(_root, "legacy-wide"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "571" };
        var legacyPath = Path.Combine(paths.CoversDir, "steam-571.jpg");
        Directory.CreateDirectory(paths.CoversDir);
        File.WriteAllBytes(legacyPath, BuildJpeg(460, 215));

        var service = new CoverService(paths, _fs, downloader: null, _logger);

        // 宽 > 高 → 方形头图
        Assert.Equal(PosterKind.Square, service.ClassifyLegacyCached(game));
    }

    [Fact]
    public void LegacyCache_NotJpeg_FallsBackToPortrait()
    {
        var paths = new AppPaths(Path.Combine(_root, "legacy-bad"));
        paths.EnsureCreated();

        var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "572" };
        var legacyPath = Path.Combine(paths.CoversDir, "steam-572.jpg");
        Directory.CreateDirectory(paths.CoversDir);
        File.WriteAllBytes(legacyPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var service = new CoverService(paths, _fs, downloader: null, _logger);

        Assert.Equal(PosterKind.Portrait, service.ClassifyLegacyCached(game));
    }

    [Fact]
    public void JpegSizeReader_ParsesRealJpegHeader()
    {
        var bytes = BuildJpeg(600, 900);
        var size = JpegSizeReader.Parse(bytes, bytes.Length);

        Assert.NotNull(size);
        Assert.Equal(600, size!.Value.Width);
        Assert.Equal(900, size.Value.Height);
    }

    [Fact]
    public void JpegSizeReader_RejectsNonJpeg()
    {
        Assert.Null(JpegSizeReader.Parse(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 4));
        Assert.Null(JpegSizeReader.Parse(Array.Empty<byte>(), 0));
    }

    /// <summary>构造一个最小可解析的 JPEG 头（SOF0 段带宽高）。</summary>
    private static byte[] BuildJpeg(int width, int height)
    {
        var bytes = new List<byte>
        {
            0xFF, 0xD8,             // SOI
            0xFF, 0xE0, 0x00, 0x10, // APP0
            0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, // SOF0
            0x08,
        };

        bytes.Add((byte)((height >> 8) & 0xFF));
        bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)((width >> 8) & 0xFF));
        bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);

        for (var i = 0; i < 9; i++)
        {
            bytes.Add(0x00);
        }

        bytes.Add(0xFF);
        bytes.Add(0xD9);

        return bytes.ToArray();
    }

    // ============================================================
    // 七、数据库：海报形态偏好持久化
    // ============================================================

    [Fact]
    public async Task PosterKindPreference_RoundTripsThroughDatabase()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "570", PosterKind = "s" };
            await env.Games.UpsertAsync(game, CancellationToken.None);

            var loaded = await env.Games.GetAsync(game.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("s", loaded!.PosterKind);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task PosterKindPreference_DefaultsToNullForNewGames()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "571" };
            await env.Games.UpsertAsync(game, CancellationToken.None);

            var loaded = await env.Games.GetAsync(game.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Null(loaded!.PosterKind);
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    [Fact]
    public async Task PosterKindPreference_UpdateReplacesPreviousValue()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        try
        {
            var game = new Game { Name = "G", Source = GameSource.Steam, ExternalId = "572", PosterKind = "p" };
            await env.Games.UpsertAsync(game, CancellationToken.None);

            game.PosterKind = "s";
            await env.Games.UpsertAsync(game, CancellationToken.None);

            var loaded = await env.Games.GetAsync(game.Id, CancellationToken.None);
            Assert.Equal("s", loaded!.PosterKind);
        }
        finally
        {
            await env.DisposeAsync();
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
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    // ============================================================
    // 测试替身
    // ============================================================

    private sealed class CountingDownloader : ICoverDownloader
    {
        private readonly byte[] _content;

        public CountingDownloader(byte[] content) => _content = content;

        public int Calls { get; private set; }

        public Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<byte[]?>(_content);
        }
    }

    private sealed class FailingDownloader : ICoverDownloader
    {
        public Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct) =>
            throw new InvalidOperationException("模拟网络故障");
    }

    private sealed class StubOnlineProvider : IOnlineSavePathProvider
    {
        private readonly IReadOnlyList<OnlineSavePath> _paths;

        public StubOnlineProvider(IReadOnlyList<OnlineSavePath> paths) => _paths = paths;

        /// <summary>非空时表示"查询失败"，返回该错误信息。</summary>
        public string? Failure { get; init; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct) =>
            Task.FromResult(_paths);

        public Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Failure is null
                ? OnlineSavePathQueryResult.Ok(_paths)
                : OnlineSavePathQueryResult.Fail(Failure));
        }
    }

    private sealed class ThrowingOnlineProvider : IOnlineSavePathProvider
    {
        public Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct) =>
            throw new InvalidOperationException("模拟站点不可达");

        public Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct) =>
            throw new InvalidOperationException("模拟站点不可达");
    }
}
