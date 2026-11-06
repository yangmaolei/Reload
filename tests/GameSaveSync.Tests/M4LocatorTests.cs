using System.Text;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>
/// M4 平台发现与规则库。全部跑在临时目录上：造 Steam / Epic / GOG / Ubisoft / UWP 的真实布局，
/// 用真实文件系统验证"能不能找到、找到的是不是对的那个目录"。
/// </summary>
public sealed class M4LocatorTests : IDisposable
{
    private readonly string _root;
    private readonly FakeLocatorEnvironment _env;
    private readonly PhysicalFileSystem _fs = PhysicalFileSystem.Instance;
    private readonly PathTokenResolver _tokens;

    public M4LocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gss-m4", Guid.NewGuid().ToString("N"));

        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(profile, "Documents"));
        Directory.CreateDirectory(Path.Combine(profile, "Saved Games"));
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming"));
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local"));
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "LocalLow"));

        _env = new FakeLocatorEnvironment
        {
            UserProfile = profile,
            AppData = Path.Combine(profile, "AppData", "Roaming"),
            LocalAppData = Path.Combine(profile, "AppData", "Local"),
            Documents = Path.Combine(profile, "Documents"),
            SavedGames = Path.Combine(profile, "Saved Games"),
            ProgramData = Path.Combine(_root, "programdata"),
        };

        Directory.CreateDirectory(_env.ProgramData);

        _tokens = new PathTokenResolver(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PathTokenResolver.UserProfile] = _env.UserProfile,
            [PathTokenResolver.AppData] = _env.AppData,
            [PathTokenResolver.LocalAppData] = _env.LocalAppData,
            [PathTokenResolver.Documents] = _env.Documents,
            [PathTokenResolver.SavedGames] = _env.SavedGames,
        });
    }

    [Fact]
    public void VdfParser_ParsesNestedBlocksAndEscapes()
    {
        const string text = """
            "libraryfolders"
            {
                // 注释不算 token
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "480" "123" "570" "456" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;

        var folders = VdfParser.ParseSingle(text)!;
        Assert.Equal(2, folders.Children.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.Child("0")!.ChildValue("path"));
        Assert.Equal("123", folders.Child("0")!.Child("apps")!.ChildValue("480"));
        Assert.Equal(@"D:\SteamLibrary", folders.Child("1")!.ChildValue("path"));
    }

    [Fact]
    public void SteamLocator_DiscoversGameAcrossLibraries()
    {
        var steam = Path.Combine(_root, "steam");
        var library2 = Path.Combine(_root, "library2");

        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path" "{{Escape(steam)}}"
                }
                "1"
                {
                    "path" "{{Escape(library2)}}"
                }
            }
            """);

        Write(Path.Combine(steam, "steamapps", "appmanifest_480.acf"), """
            "AppState"
            {
                "appid"		"480"
                "name"		"Spacewar"
                "StateFlags"	"4"
                "installdir"	"Spacewar"
            }
            """);

        Write(Path.Combine(steam, "steamapps", "common", "Spacewar", "Spacewar.exe"), "exe");
        Write(Path.Combine(steam, "steamapps", "common", "Spacewar", "UnityPlayer.dll"), "dll");
        Write(Path.Combine(steam, "userdata", "12345", "480", "remote", "profile.sav"), "save");

        // 第二个库里的游戏也应当被发现
        Write(Path.Combine(library2, "steamapps", "appmanifest_570.acf"), """
            "AppState"
            {
                "appid"		"570"
                "name"		"Dota 2"
                "StateFlags"	"4"
                "installdir"	"dota 2 beta"
            }
            """);
        Write(Path.Combine(library2, "steamapps", "common", "dota 2 beta", "dota2.exe"), "exe");

        _env.SteamRoots.Add(steam);

        var games = new SteamLocator(_fs, _env).Scan(out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, games.Count);

        var spacewar = games.Single(g => g.ExternalId == "480");
        Assert.Equal("Spacewar", spacewar.Name);
        Assert.Equal(GameSource.Steam, spacewar.Source);
        Assert.Contains("Spacewar.exe", spacewar.ExeNames);
        Assert.Equal(GameEngine.Unity, spacewar.Engine);
        Assert.Contains(spacewar.SuggestedRoots,
            r => r.EndsWith(@"userdata\12345\480\remote", StringComparison.OrdinalIgnoreCase));

        var dota = games.Single(g => g.ExternalId == "570");
        Assert.True(File.Exists(Path.Combine(dota.InstallDir!, "dota2.exe")));
    }

    [Fact]
    public void EpicLocator_ParsesManifestJson()
    {
        var manifests = Path.Combine(_env.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var install = Path.Combine(_root, "EpicGames", "MyEpicGame");
        Write(Path.Combine(install, "MyEpicGame.exe"), "exe");
        Write(Path.Combine(install, "Engine", "Binaries", "Win64", "MyEpicGame.exe"), "exe");
        Write(Path.Combine(_env.LocalAppData, "MyEpicGame", "Saved", "SaveGames", "slot1.sav"), "save");

        Write(Path.Combine(manifests, "abc.item"), $$"""
            {
                "DisplayName": "My Epic Game",
                "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
                "CatalogItemId": "cat-1",
                "AppName": "MyEpicGame"
            }
            """);

        _env.EpicManifestDirs.Add(manifests);

        var games = new EpicLocator(_fs, _env).Scan(out var warnings);
        Assert.Empty(warnings);
        var game = Assert.Single(games);
        Assert.Equal("My Epic Game", game.Name);
        Assert.Equal(GameSource.Epic, game.Source);
        Assert.Equal(GameEngine.Unreal, game.Engine);

        var resolver = new SavePathResolver(_fs, _tokens);
        var roots = resolver.ResolveRoots(game);
        Assert.Contains(roots, r => r.EndsWith(@"MyEpicGame\Saved\SaveGames", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GogLocator_ReadsGoggameInfo()
    {
        var gogRoot = Path.Combine(_root, "GOG Games");
        var install = Path.Combine(gogRoot, "Installed Folder");
        Write(Path.Combine(install, "game.exe"), "exe");
        Write(Path.Combine(install, "goggame-123456.info"), """
            { "gameId": "123456", "name": "GOG Classic" }
            """);

        _env.GogGameDirs.Add(gogRoot);

        var games = new GogLocator(_fs, _env).Scan(out _);
        var game = Assert.Single(games);
        Assert.Equal("GOG Classic", game.Name);
        Assert.Equal("123456", game.ExternalId);
        Assert.Equal(GameSource.Gog, game.Source);
    }

    [Fact]
    public void UbisoftLocator_ListsSaveFolders()
    {
        var saves = Path.Combine(_env.LocalAppData, "Ubisoft Game Launcher", "savegames");
        Write(Path.Combine(saves, "user-1", "635", "1.save"), "save");
        Write(Path.Combine(saves, "user-1", "720", "slot.dat"), "save");
        _env.UbisoftSaveRoots.Add(saves);

        var games = new UbisoftLocator(_fs, _env).Scan(out _);

        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.Equal(GameSource.Ubisoft, g.Source));
        Assert.Contains(games, g => g.ExternalId == "user-1/635" && g.SuggestedRoots.Count == 1);
    }

    [Fact]
    public void XboxLocator_FindsWgsContainer()
    {
        const string pfn = "Studio.GameName_abc123";
        var install = Path.Combine(_root, "WindowsApps", pfn);
        Directory.CreateDirectory(install);
        Write(Path.Combine(install, "AppxManifest.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Studio.GameName" Publisher="CN=Test" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Properties><DisplayName>Xbox Game</DisplayName></Properties>
              <Applications>
                <Application Id="Game" Executable="GameLaunchHelper.exe" EntryPoint="Windows.FullTrustApplication" />
              </Applications>
            </Package>
            """);

        Write(Path.Combine(_env.LocalAppData, "Packages", pfn, "SystemAppData", "wgs", "container.1"), "save");
        _env.UwpPackages.Add(new UwpPackage(pfn, "Xbox Game", install));

        var games = new XboxLocator(_fs, _env).Scan(out _);
        var game = Assert.Single(games);
        Assert.Equal("Xbox Game", game.Name);
        Assert.Equal(GameSource.Xbox, game.Source);
        Assert.Contains(game.SuggestedRoots, r => r.EndsWith(@"SystemAppData\wgs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavePathResolver_FindsMyGamesByRule()
    {
        var saves = Path.Combine(_env.Documents, "My Games", "Test Game", "Saves");
        Write(Path.Combine(saves, "slot1.sav"), "save");

        var game = new DiscoveredGame { Name = "Test Game", Source = GameSource.Manual };
        var roots = new SavePathResolver(_fs, _tokens).ResolveRoots(game);

        Assert.Contains(roots, r => r.Equals(saves, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavePathResolver_UsesUnityLocalLowRadar()
    {
        var product = Path.Combine(_env.UserProfile, "AppData", "LocalLow", "Some Studio", "Test Game");
        Write(Path.Combine(product, "profile.dat"), "save");

        var install = Path.Combine(_root, "unitygame");
        Write(Path.Combine(install, "TestGame.exe"), "exe");
        Write(Path.Combine(install, "UnityPlayer.dll"), "dll");

        var game = new DiscoveredGame
        {
            Name = "Test Game",
            Source = GameSource.Steam,
            InstallDir = install,
            Engine = GameEngine.Unity,
        };

        var roots = new SavePathResolver(_fs, _tokens).ResolveRoots(game);
        Assert.Contains(roots, r => r.Equals(product, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavePathRuleSet_ExternalOverrideReplacesAndAdds()
    {
        var path = Path.Combine(_root, "save-paths.yaml");
        Write(path, """
            version: 2
            rules:
              - id: my-games
                description: 我自己的 My Games 规则
                namePattern: "*"
                roots:
                  - %DOCUMENTS%\My Games\{name}\Custom
              - id: custom-only
                description: 完全自定义
                namePattern: "Test*"
                roots:
                  - %DOCUMENTS%\CustomSaves\{name}
            """);

        var rules = SavePathRuleSet.Load(path, _fs, out var warning);

        Assert.Null(warning);
        Assert.Equal(2, rules.Version);
        var replaced = rules.Rules.Single(r => r.Id == "my-games");
        Assert.Single(replaced.Roots);
        Assert.Contains(@"Custom", replaced.Roots[0], StringComparison.Ordinal);
        Assert.Contains(rules.Rules, r => r.Id == "custom-only");
        Assert.Contains(rules.Rules, r => r.Id == "documents-game");   // 其余内置规则保留

        var custom = Path.Combine(_env.Documents, "CustomSaves", "TestGame");
        Write(Path.Combine(custom, "a.sav"), "save");

        var game = new DiscoveredGame { Name = "TestGame", Source = GameSource.Manual };
        var roots = new SavePathResolver(_fs, _tokens, rules).ResolveRoots(game);
        Assert.Contains(roots, r => r.Equals(custom, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GameDiscovery_MarksAlreadyAddedAndSkipsDuplicates()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var steam = Path.Combine(_root, "steam2");
        Write(Path.Combine(steam, "steamapps", "appmanifest_100.acf"), """
            "AppState"
            {
                "appid"		"100"
                "name"		"Duplicate Game"
                "StateFlags"	"4"
                "installdir"	"Duplicate"
            }
            """);
        Write(Path.Combine(steam, "steamapps", "common", "Duplicate", "dup.exe"), "exe");
        _env.SteamRoots.Add(steam);

        await env.AddGameAsync("Duplicate Game", default);

        var service = new GameDiscoveryService(
            _fs, _env, _tokens, env.Games, env.Logger, SavePathRuleSet.BuiltIn);

        var result = await service.ScanAsync(new DiscoveryOptions
        {
            IncludeEpic = false,
            IncludeGog = false,
            IncludeUbisoft = false,
            IncludeXbox = false,
        }, CancellationToken.None);

        var game = Assert.Single(result.Games);
        Assert.True(game.AlreadyAdded);
        Assert.Equal("Duplicate Game", game.Name);
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

    private string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>VDF 里反斜杠要写两遍。</summary>
    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class FakeLocatorEnvironment : ILocatorEnvironment
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
