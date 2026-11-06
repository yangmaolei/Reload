using System.Text;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// M6：Xbox / 微软商店游戏识别。
/// <para>
/// 覆盖三类来源：MSIX(GDK) 游戏包（清单 GameLaunchHelper.exe / ms-xbl 协议 / MicrosoftGame.Config）、
/// 包数据目录里的 wgs 云存档容器、以及 Xbox 原生游戏库（&lt;盘符&gt;\XboxGames）下的 pgs 云存档。
/// </para>
/// </summary>
public sealed class M6XboxTests : IDisposable
{
    private readonly string _root;
    private readonly FakeEnv _env;
    private readonly PhysicalFileSystem _fs = PhysicalFileSystem.Instance;

    public M6XboxTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gss-m6", Guid.NewGuid().ToString("N"));

        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);

        _env = new FakeEnv
        {
            UserProfile = profile,
            AppData = Path.Combine(profile, "AppData", "Roaming"),
            LocalAppData = Path.Combine(profile, "AppData", "Local"),
            Documents = Path.Combine(profile, "Documents"),
            SavedGames = Path.Combine(profile, "Saved Games"),
            ProgramData = Path.Combine(_root, "programdata"),
        };

        Directory.CreateDirectory(_env.ProgramData);
        Directory.CreateDirectory(_env.LocalAppData);
    }

    // ────────────────────────── 包名派生 ──────────────────────────

    [Theory]
    [InlineData("CRITICALREFLEX.BuckshotRoulette_1.0.7.0_x64__20ymd9g5r2c54",
        "CRITICALREFLEX.BuckshotRoulette_20ymd9g5r2c54")]
    [InlineData("Microsoft.ForteBaseGame_3.440.853.0_x64__8wekyb3d8bbwe",
        "Microsoft.ForteBaseGame_8wekyb3d8bbwe")]
    [InlineData("TeamCherry.HollowKnightSilksong_1.0.30000.0_x64__y4jvztpgccj42",
        "TeamCherry.HollowKnightSilksong_y4jvztpgccj42")]
    [InlineData("Microsoft.XboxIdentityProvider_25.101.21001.0_x64__8wekyb3d8bbwe",
        "Microsoft.XboxIdentityProvider_8wekyb3d8bbwe")]
    public void DerivePackageFamilyName_StripsVersionSegment(string fullName, string expected)
    {
        Assert.Equal(expected, WindowsLocatorEnvironment.DerivePackageFamilyName(fullName));
    }

    [Theory]
    [InlineData("Studio.GameName_abc123")]
    [InlineData("Name_1.0_x64")]
    public void DerivePackageFamilyName_KeepsAlreadyFamilyShapedNames(string value)
    {
        Assert.Equal(value, WindowsLocatorEnvironment.DerivePackageFamilyName(value));
    }

    // ────────────────────────── MSIX 游戏包 ──────────────────────────

    [Fact]
    public void XboxLocator_FindsMsixGameWithWgsContainer()
    {
        const string pfn = "TeamCherry.HollowKnightSilksong_y4jvztpgccj42";
        var install = CreateMsixPackage(pfn, "Hollow Knight: Silksong", "Hollow.Knight.Silksong", "636F5860");
        Write(Path.Combine(install, "Hollow Knight Silksong.exe"), "exe");
        Write(Path.Combine(install, "UnityPlayer.dll"), "dll");
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "000901FE", "containers.index"), "save");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Hollow Knight: Silksong", install));

        var games = new XboxLocator(_fs, _env).Scan(out var warnings);

        var game = Assert.Single(games);
        Assert.Equal("Hollow Knight: Silksong", game.Name);
        Assert.Equal(GameSource.Xbox, game.Source);
        Assert.Equal(pfn, game.ExternalId);
        Assert.Equal(install, game.InstallDir);
        Assert.Equal(GameEngine.Unity, game.Engine);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith(@"SystemAppData\wgs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(game.Notes, n => n.Contains("636F5860"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void XboxLocator_IgnoresOrdinaryStoreApps()
    {
        const string pfn = "Clipchamp.Clipchamp_yxz26nhyzhsrt";
        var install = CreateMsixPackage(pfn, "Clipchamp", "App", null, gameLauncher: false);
        Write(Path.Combine(install, "Clipchamp.exe"), "exe");
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "LocalState", "cache.bin"), "blob");

        var games = new XboxLocator(_fs, _env).Scan(out _);

        Assert.Empty(games);
    }

    [Fact]
    public void XboxLocator_ExcludesXboxRuntimePackages()
    {
        const string pfn = "Microsoft.GamingApp_8wekyb3d8bbwe";
        var install = CreateMsixPackage(pfn, "Xbox App", "App", null);
        Write(Path.Combine(install, "GameLaunchHelper.exe"), "exe");
        // 就算 Xbox 应用自己带 wgs，也不能当成游戏
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "x"), "save");

        var games = new XboxLocator(_fs, _env).Scan(out _);

        Assert.Empty(games);
    }

    [Fact]
    public void XboxLocator_FindsGdkGameByConfigFile()
    {
        const string pfn = "Microsoft.ForteBaseGame_8wekyb3d8bbwe";
        var install = Path.Combine(_root, "WindowsApps", pfn);
        Directory.CreateDirectory(install);
        Write(Path.Combine(install, "MicrosoftGame.Config"), "config");
        Write(Path.Combine(install, "ForzaHorizon6.exe"), "exe");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Forza Horizon 6", install));

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Equal("Forza Horizon 6", game.Name);
        Assert.Equal(GameSource.Xbox, game.Source);
    }

    [Fact]
    public void XboxLocator_KeepsEmptyWgsDirectoryAsSaveRoot()
    {
        const string pfn = "OutrightGames.MyFriendPeppaPig_bnrbc155f0j86";
        var install = CreateMsixPackage(pfn, "My Friend Peppa Pig", "Game", "7972AC43");
        Directory.CreateDirectory(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs"));
        _env.UwpPackages.Add(new UwpPackage(pfn, "My Friend Peppa Pig", install));

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith(@"SystemAppData\wgs", StringComparison.OrdinalIgnoreCase));

        // 规则解析阶段不能因为目录为空就丢掉它（游戏还没存档）
        var resolver = new SavePathResolver(_fs, BuildTokens());
        var roots = resolver.ResolveRoots(game);
        Assert.Contains(roots, r => r.EndsWith(@"SystemAppData\wgs", StringComparison.OrdinalIgnoreCase));
    }

    // ────────────────────────── 云存档归属 ──────────────────────────

    [Fact]
    public void XboxLocator_AttachesPgsCloudSaveByAumidPackageFamily()
    {
        const string pfn = "Microsoft.ForteBaseGame_8wekyb3d8bbwe";
        var install = CreateMsixPackage(pfn, "Forza Horizon 6", "Forzahorizon6", "7BF69384");
        Write(Path.Combine(install, "MicrosoftGame.Config"), "config");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Forza Horizon 6", install));

        var library = Path.Combine(_root, "XboxGames");
        var pgs = Path.Combine(library, "GameSave", "pgs", "u_2535467423605544_16D460");
        Write(Path.Combine(pgs, "u_2535467423605544_16D460.json"),
            """
            {"Context":{"Aumid":"Microsoft.ForteBaseGame_8wekyb3d8bbwe!Forzahorizon6",
            "PackageFullName":"Microsoft.ForteBaseGame_3.430.771.0_x64__8wekyb3d8bbwe","TotalSize":"1"}}
            """);
        Write(Path.Combine(pgs, "current"), "blob");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Equal("Forza Horizon 6", game.Name);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith("u_2535467423605544_16D460", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XboxLocator_AttachesPgsCloudSaveByApplicationId()
    {
        // AUMID 里的包族名在本机没有对应包（游戏已经被卸载），只能靠 ApplicationId 兜底
        const string pfn = "Studio.Silksong_y4jvztpgccj42";
        var install = CreateMsixPackage(pfn, "Silksong", "Hollow.Knight.Silksong", null);
        _env.UwpPackages.Add(new UwpPackage(pfn, "Silksong", install));

        var library = Path.Combine(_root, "XboxGames");
        var pgs = Path.Combine(library, "GameSave", "pgs", "u_111_222222");
        Write(Path.Combine(pgs, "u_111_222222.json"),
            """{"Context":{"Aumid":"Other.Publisher_abcdefgh!Hollow.Knight.Silksong"}}""");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith("u_111_222222", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XboxLocator_AttachesSharedWgsContainerByTitleId()
    {
        const string pfn = "Studio.Silksong_y4jvztpgccj42";
        var install = CreateMsixPackage(pfn, "Silksong", "Silksong", "636F5860");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Silksong", install));

        var library = Path.Combine(_root, "XboxGames");
        var wgs = Path.Combine(library, "GameSave", "wgs", "636F5860");
        Write(Path.Combine(wgs, "container"), "blob");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith(@"GameSave\wgs\636F5860", StringComparison.OrdinalIgnoreCase));
    }

    // ────────────────────────── 原生游戏库 ──────────────────────────

    [Fact]
    public void XboxLocator_FindsNativeLibraryGame()
    {
        var library = Path.Combine(_root, "XboxGames");
        var title = Path.Combine(library, "My Native Game");
        Write(Path.Combine(title, "game.exe"), "exe");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Equal("My Native Game", game.Name);
        Assert.Equal(GameSource.Xbox, game.Source);
        Assert.Equal("xbox-native:My Native Game", game.ExternalId);
        Assert.Equal(title, game.InstallDir);
    }

    [Fact]
    public void XboxLocator_SkipsGameSaveFolderInNativeLibrary()
    {
        var library = Path.Combine(_root, "XboxGames");
        Write(Path.Combine(library, "GameSave", "pgs", "u_1_2", "x"), "blob");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        Assert.Empty(games);
    }

    [Fact]
    public void XboxLocator_DoesNotDuplicateNativeGameAlreadyCoveredByPackage()
    {
        const string pfn = "Microsoft.ForteBaseGame_8wekyb3d8bbwe";
        var install = CreateMsixPackage(pfn, "Forza Horizon 6", "Forzahorizon6", "7BF69384");
        Write(Path.Combine(install, "MicrosoftGame.Config"), "config");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Forza Horizon 6", install));

        var library = Path.Combine(_root, "XboxGames");
        Write(Path.Combine(library, "Forza Horizon 6", "Content", "blob"), "blob");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Equal("Forza Horizon 6", game.Name);
    }

    [Fact]
    public void XboxLocator_AttachesPgsCloudSaveToNativeOnlyGame()
    {
        // 只有原生安装、没有 MSIX 包的游戏，云存档要靠 AUMID 的应用 ID 对上标题
        var library = Path.Combine(_root, "XboxGames");
        Write(Path.Combine(library, "Native Only Game", "Content", "blob"), "blob");

        var pgs = Path.Combine(library, "GameSave", "pgs", "u_999_ABCDEF");
        Write(Path.Combine(pgs, "999_ABCDEF.json"),
            """{"Context":{"Aumid":"Some.Publisher_xyz!NativeOnlyGame"}}""");
        _env.XboxLibraryRoots.Add(library);

        var games = new XboxLocator(_fs, _env).Scan(out _);

        var game = Assert.Single(games);
        Assert.Equal("Native Only Game", game.Name);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith("u_999_ABCDEF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XboxLocator_ExcludesAddonPackages()
    {
        // DLC 附属包会声明对主游戏包的依赖，不该被当成独立游戏
        const string pfn = "Publisher.SomeGameDLC_abc123";
        var install = Path.Combine(_root, "WindowsApps", pfn);
        Directory.CreateDirectory(install);

        var manifest = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Publisher.SomeGameDLC" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>Some Game - Season Pass</DisplayName>
              </Properties>
              <Dependencies>
                <PackageDependency Name="Publisher.SomeGame_abc123" />
              </Dependencies>
              <Applications>
                <Application Id="Game" Executable="GameLaunchHelper.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """;

        Write(Path.Combine(install, "AppxManifest.xml"), manifest);
        _env.UwpPackages.Add(new UwpPackage(pfn, "Some Game - Season Pass", install));

        var games = new XboxLocator(_fs, _env).Scan(out _);

        Assert.Empty(games);
    }

    [Fact]
    public void XboxLocator_ExcludesUninstalledPackages()
    {
        // 注册表里还留着，但安装目录已经被清掉（侧载包卸载后的残留）
        const string pfn = "Publisher.Orphan_abc123";
        _env.UwpPackages.Add(new UwpPackage(pfn, "Orphan Game", Path.Combine(_root, "WindowsApps", pfn)));
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "x"), "save");

        var games = new XboxLocator(_fs, _env).Scan(out _);

        Assert.Empty(games);
    }

    // ────────────────────────── 端到端 ──────────────────────────

    [Fact]
    public async Task GameDiscoveryService_IncludesXboxGames()
    {
        const string pfn = "Studio.CoolGame_abc123";
        var install = CreateMsixPackage(pfn, "Cool Game", "Game", "00112233");
        Write(Path.Combine(install, "CoolGame.exe"), "exe");
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "c"), "save");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Cool Game", install));

        await using var env = TestEnvironment.Create(withWebDav: false);

        var service = new GameDiscoveryService(
            _fs, _env, BuildTokens(), env.Games, env.Logger, SavePathRuleSet.BuiltIn);

        var result = await service.ScanAsync(new DiscoveryOptions(), CancellationToken.None);

        var game = Assert.Single(result.Games);
        Assert.Equal("Cool Game", game.Name);
        Assert.Equal(GameSource.Xbox, game.Source);
        Assert.Single(game.SuggestedRoots);
    }

    [Fact]
    public async Task GameDiscoveryService_CanSkipXbox()
    {
        const string pfn = "Studio.CoolGame_abc123";
        var install = CreateMsixPackage(pfn, "Cool Game", "Game", "00112233");
        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "c"), "save");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Cool Game", install));

        await using var env = TestEnvironment.Create(withWebDav: false);

        var service = new GameDiscoveryService(
            _fs, _env, BuildTokens(), env.Games, env.Logger, SavePathRuleSet.BuiltIn);

        var result = await service.ScanAsync(new DiscoveryOptions
        {
            IncludeSteam = false,
            IncludeEpic = false,
            IncludeGog = false,
            IncludeUbisoft = false,
            IncludeXbox = false,
        }, CancellationToken.None);

        Assert.Empty(result.Games);
    }

    // ────────────────────────── 辅助 ──────────────────────────

    private PathTokenResolver BuildTokens() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [PathTokenResolver.UserProfile] = _env.UserProfile,
        [PathTokenResolver.AppData] = _env.AppData,
        [PathTokenResolver.LocalAppData] = _env.LocalAppData,
        [PathTokenResolver.Documents] = _env.Documents,
        [PathTokenResolver.SavedGames] = _env.SavedGames,
    });

    /// <summary>造一个 MSIX 包目录（含 AppxManifest.xml）。</summary>
    private string CreateMsixPackage(
        string familyName,
        string displayName,
        string applicationId,
        string? titleId,
        bool gameLauncher = true)
    {
        var install = Path.Combine(_root, "WindowsApps", familyName);
        Directory.CreateDirectory(install);

        var identity = familyName.Contains('_', StringComparison.Ordinal)
            ? familyName[..familyName.IndexOf('_', StringComparison.Ordinal)]
            : familyName;

        var protocol = titleId is null
            ? string.Empty
            : $"""
               <Extensions>
                 <uap:Extension Category="windows.protocol" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
                   <uap:Protocol Name="ms-xbl-{titleId}" />
                 </uap:Extension>
               </Extensions>
               """;

        var executable = gameLauncher ? "GameLaunchHelper.exe" : "App.exe";

        var manifest = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{identity}" Publisher="CN=Test Publisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties>
                <DisplayName>{displayName}</DisplayName>
                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                <Logo>StoreLogo.png</Logo>
              </Properties>
              <Applications>
                <Application Id="{applicationId}" Executable="{executable}" EntryPoint="Windows.FullTrustApplication">
                  {protocol}
                </Application>
              </Applications>
            </Package>
            """;

        Write(Path.Combine(install, "AppxManifest.xml"), manifest);
        return install;
    }

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
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
            // 临时目录清理失败不影响测试结论
        }
    }

    private sealed class FakeEnv : ILocatorEnvironment
    {
        public string UserProfile { get; set; } = string.Empty;

        public string AppData { get; set; } = string.Empty;

        public string LocalAppData { get; set; } = string.Empty;

        public string Documents { get; set; } = string.Empty;

        public string SavedGames { get; set; } = string.Empty;

        public string ProgramData { get; set; } = string.Empty;

        public List<string> SteamRoots { get; } = new();

        public List<string> EpicManifestDirs { get; } = new();

        public List<string> GogGameDirs { get; } = new();

        public List<string> UbisoftSaveRoots { get; } = new();

        public List<UwpPackage> UwpPackages { get; } = new();

        public List<string> XboxLibraryRoots { get; } = new();

        public IReadOnlyList<string> GetSteamRoots() => SteamRoots;

        public IReadOnlyList<string> GetEpicManifestDirs() => EpicManifestDirs;

        public IReadOnlyList<string> GetGogGameDirs() => GogGameDirs;

        public IReadOnlyList<string> GetUbisoftSaveRoots() => UbisoftSaveRoots;

        public IReadOnlyList<UwpPackage> GetUwpPackages() => UwpPackages;

        public IReadOnlyList<string> GetXboxLibraryRoots() => XboxLibraryRoots;

        public IReadOnlyDictionary<string, string> GetExtraPathTokens() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
