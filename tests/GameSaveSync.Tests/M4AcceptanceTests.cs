using System.Text;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Probe;
using GameSaveSync.Core.Restore;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;
using Xunit.Abstractions;

namespace GameSaveSync.Tests;

/// <summary>
/// M4 交付验收 6 条：自动发现 → 规则匹配 → 快照、探测模式定位、便携模式、
/// 差异视图、引擎识别、外部规则覆盖。全部跑在真实临时目录上。
/// </summary>
public sealed class M4AcceptanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly PhysicalFileSystem _fs = PhysicalFileSystem.Instance;
    private readonly InMemoryAppLogger _logger = new(500);

    public M4AcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "gss-m4acc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task D1_SteamDiscovery_ToSnapshot_RoundTrip()
    {
        var steam = Path.Combine(_root, "steam");
        await using var env = TestEnvironment.Create(withWebDav: false, extraTokens:
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["%STEAMUSERDATA%"] = Path.Combine(steam, "userdata"),
            });

        Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Escape(steam)}}" }
            }
            """);
        Write(Path.Combine(steam, "steamapps", "appmanifest_480.acf"), """
            "AppState"
            {
                "appid" "480"
                "name" "Spacewar"
                "StateFlags" "4"
                "installdir" "Spacewar"
            }
            """);
        Write(Path.Combine(steam, "steamapps", "common", "Spacewar", "Spacewar.exe"), "exe");
        Write(Path.Combine(steam, "steamapps", "common", "Spacewar", "UnityPlayer.dll"), "dll");
        Write(Path.Combine(steam, "userdata", "12345", "480", "remote", "profile.sav"), "save-1");

        var localLow = Path.Combine(env.ProfileDir, "AppData", "LocalLow", "Studio", "Spacewar");
        Write(Path.Combine(localLow, "settings.dat"), "settings");

        var locatorEnv = new TestLocatorEnvironment
        {
            UserProfile = env.ProfileDir,
            AppData = Path.Combine(env.ProfileDir, "AppData", "Roaming"),
            LocalAppData = Path.Combine(env.ProfileDir, "AppData", "Local"),
            Documents = Path.Combine(env.ProfileDir, "Documents"),
            SavedGames = env.SaveGamesDir,
            ProgramData = Path.Combine(_root, "programdata"),
        };
        locatorEnv.SteamRoots.Add(steam);
        Directory.CreateDirectory(locatorEnv.ProgramData);

        var discovery = new GameDiscoveryService(
            _fs, locatorEnv, env.Tokens, env.Games, _logger, SavePathRuleSet.BuiltIn);

        var result = await discovery.ScanAsync(new DiscoveryOptions
        {
            IncludeEpic = false,
            IncludeGog = false,
            IncludeUbisoft = false,
            IncludeXbox = false,
        }, CancellationToken.None);

        var discovered = Assert.Single(result.Games);
        Assert.Equal(GameEngine.Unity, discovered.Engine);
        Assert.Contains(discovered.SuggestedRoots, r => r.Equals(localLow, StringComparison.OrdinalIgnoreCase));

        var game = discovery.CreateGame(discovered, discovered.SuggestedRoots, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await env.Games.UpsertAsync(game, CancellationToken.None);

        var snapshot = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, "M4-D1", CancellationToken.None);

        Assert.NotNull(snapshot.SnapshotId);
        Assert.True(snapshot.FileCount >= 2);

        var restored = await env.RestoreService.PlanAsync(snapshot.SnapshotId!.Value, CancellationToken.None);
        Assert.True(restored.CanExecute);

        _output.WriteLine($"[D1] 发现 {discovered.Name}（{discovered.PlatformLabel}/{discovered.Engine}），" +
                          $"自动匹配 {discovered.SuggestedRoots.Count} 个存档根，快照 {snapshot.FileCount} 个文件 / " +
                          $"{snapshot.TotalSize} B，恢复计划 {restored.Operations.Count} 项可执行");
    }

    [Fact]
    public async Task D2_ProbeMode_FindsSaveDirectoryCreatedWhilePlaying()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var documents = Path.Combine(env.ProfileDir, "Documents");
        var saves = Path.Combine(documents, "My Games", "Probe Target", "Saves");
        Write(Path.Combine(saves, "slot1.sav"), "before");

        var probe = new ProbeService(_fs, env.Clock, _logger);
        var start = await probe.StartAsync(new ProbeRoots(new[] { documents }), null, CancellationToken.None);

        // 用户"玩游戏"期间：新增两个槽位、改了一个
        Write(Path.Combine(saves, "slot2.sav"), "after-2");
        Write(Path.Combine(saves, "slot3.sav"), "after-3");
        Write(Path.Combine(saves, "slot1.sav"), "after-1-longer");

        var result = await probe.CompleteAsync(start, null, CancellationToken.None);
        var top = Assert.Single(result.Candidates);
        Assert.Equal(saves, top.Directory);

        var game = await env.AddGameAsync("Probe Target", default, top.Directory);
        var snapshot = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, "M4-D2", CancellationToken.None);

        Assert.Equal(3, snapshot.FileCount);

        _output.WriteLine($"[D2] 探测记录 {start.FileCount} 个文件，检测到 {result.ChangedFiles} 个变化，" +
                          $"首选候选 {top.Directory}（新增 {top.NewFiles} / 修改 {top.ModifiedFiles} / 评分 {top.Score}），" +
                          $"加入后快照 {snapshot.FileCount} 个文件");
    }

    [Fact]
    public void D3_PortableMode_PutsDataNextToExecutable()
    {
        var appDir = Path.Combine(_root, "portable-app");
        var localAppData = Path.Combine(_root, "local");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(localAppData);

        var normal = AppPaths.ResolveDefault(appDir, localAppData);
        Assert.False(normal.IsPortable);
        Assert.StartsWith(localAppData, normal.DataRoot, StringComparison.OrdinalIgnoreCase);

        Write(Path.Combine(appDir, AppPaths.PortableFlagName), "portable");
        var portable = AppPaths.ResolveDefault(appDir, localAppData);
        portable.EnsureCreated();

        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(appDir, "data"), portable.DataRoot);
        Assert.True(Directory.Exists(portable.ObjectsDir));
        Assert.True(Directory.Exists(portable.LogsDir));
        Assert.True(Directory.Exists(portable.CoversDir));
        Assert.False(portable.DatabasePath.Contains(localAppData, StringComparison.OrdinalIgnoreCase));

        _output.WriteLine($"[D3] 常规模式数据目录 {normal.DataRoot}；便携模式 {portable.DataRoot}（无注册表写入）");
    }

    [Fact]
    public async Task D4_DiffView_ShowsAddedModifiedRemoved()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir(
            "Saved Games/DiffAcceptance",
            ("a.sav", TestEnvironment.Bytes(256, seed: 1)),
            ("b.sav", TestEnvironment.Bytes(512, seed: 2)));

        var game = await env.AddGameAsync("Diff Acceptance", default, dir);
        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "s1", CancellationToken.None);

        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(1024, seed: 3));
        File.Delete(Path.Combine(dir, "b.sav"));
        File.WriteAllBytes(Path.Combine(dir, "c.sav"), TestEnvironment.Bytes(64, seed: 4));

        var second = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Exit, "s2", CancellationToken.None);
        var diff = await new SnapshotDiffService(env.Snapshots)
            .CompareWithPreviousAsync(second.SnapshotId!.Value, CancellationToken.None);

        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.ModifiedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(3, diff.Entries.Count);

        _output.WriteLine($"[D4] 相对上一快照：{diff.AddedCount} 新增 / {diff.ModifiedCount} 修改 / " +
                          $"{diff.RemovedCount} 删除，体积 {diff.SizeBefore} → {diff.SizeAfter} B " +
                          $"（{diff.SizeDelta:+#;-#;0} B）；首个快照 {first.SnapshotId:N}");
    }

    [Fact]
    public void D5_EngineDetection_CoversFourCommonEngines()
    {
        var unity = Path.Combine(_root, "unity");
        Write(Path.Combine(unity, "Game.exe"), "exe");
        Write(Path.Combine(unity, "UnityPlayer.dll"), "dll");

        var unreal = Path.Combine(_root, "unreal");
        Write(Path.Combine(unreal, "Game.exe"), "exe");
        Write(Path.Combine(unreal, "Engine", "Binaries", "Win64", "Game.exe"), "exe");

        var rpgMaker = Path.Combine(_root, "rpgmaker");
        Write(Path.Combine(rpgMaker, "Game.exe"), "exe");
        Write(Path.Combine(rpgMaker, "www", "js", "rpg_core.js"), "js");

        var renpy = Path.Combine(_root, "renpy");
        Write(Path.Combine(renpy, "renpy.exe"), "exe");
        Write(Path.Combine(renpy, "game", "script.rpy"), "script");

        var gameMaker = Path.Combine(_root, "gamemaker");
        Write(Path.Combine(gameMaker, "data.win"), "data");

        var results = new[]
        {
            ("Unity", EngineDetector.Detect(_fs, unity), GameEngine.Unity),
            ("Unreal", EngineDetector.Detect(_fs, unreal), GameEngine.Unreal),
            ("RPG Maker", EngineDetector.Detect(_fs, rpgMaker), GameEngine.RpgMaker),
            ("Ren'Py", EngineDetector.Detect(_fs, renpy), GameEngine.RenPy),
            ("GameMaker", EngineDetector.Detect(_fs, gameMaker), GameEngine.GameMaker),
        };

        foreach (var (name, actual, expected) in results)
        {
            Assert.Equal(expected, actual);
            _output.WriteLine($"[D5] {name} → {actual}");
        }
    }

    [Fact]
    public async Task D6_ExternalRuleFile_OverridesBuiltIn()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);

        var customRoot = Path.Combine(env.ProfileDir, "Documents", "MyCompany", "CustomGame");
        Write(Path.Combine(customRoot, "profile.sav"), "save");

        var ruleFile = Path.Combine(env.Root, "save-paths.yaml");
        Write(ruleFile, """
            version: 3
            rules:
              - id: company-layout
                description: 自定义发行商目录
                namePattern: "Custom*"
                roots:
                  - %DOCUMENTS%\MyCompany\{name}
            """);

        var rules = SavePathRuleSet.Load(ruleFile, _fs, out var warning);
        Assert.Null(warning);
        Assert.Equal(3, rules.Version);

        var game = new DiscoveredGame { Name = "CustomGame", Source = GameSource.Manual };
        var roots = new SavePathResolver(_fs, env.Tokens, rules).ResolveRoots(game);

        Assert.Contains(roots, r => r.Equals(customRoot, StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"[D6] 外部规则共 {rules.Rules.Count} 条（内置 + 自定义），命中目录 {customRoot}");
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
            // 临时目录清理失败不影响结论
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class TestLocatorEnvironment : ILocatorEnvironment
    {
        public string UserProfile { get; set; } = string.Empty;

        public string AppData { get; set; } = string.Empty;

        public string LocalAppData { get; set; } = string.Empty;

        public string Documents { get; set; } = string.Empty;

        public string SavedGames { get; set; } = string.Empty;

        public string ProgramData { get; set; } = string.Empty;

        public List<string> SteamRoots { get; } = new();

        public IReadOnlyList<string> GetSteamRoots() => SteamRoots;

        public IReadOnlyList<string> GetEpicManifestDirs() => Array.Empty<string>();

        public IReadOnlyList<string> GetGogGameDirs() => Array.Empty<string>();

        public IReadOnlyList<string> GetUbisoftSaveRoots() => Array.Empty<string>();

        public IReadOnlyList<UwpPackage> GetUwpPackages() => Array.Empty<UwpPackage>();

        public IReadOnlyList<string> GetXboxLibraryRoots() => Array.Empty<string>();

        public IReadOnlyDictionary<string, string> GetExtraPathTokens() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
