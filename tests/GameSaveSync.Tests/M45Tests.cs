using GameSaveSync.Core.Configuration;
using GameSaveSync.Tests.TestSupport;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Theming;

namespace GameSaveSync.Tests;

/// <summary>M4.5：改名 Reload、主题色板、全局排除。</summary>
public class M45Tests
{
    // ===== 主题色板 =====

    [Fact]
    public void ThemePalettes_AllSeedsProduceCompleteRoleSets()
    {
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Primary", "OnPrimary", "PrimaryContainer", "OnPrimaryContainer",
            "Secondary", "OnSecondary", "SecondaryContainer", "OnSecondaryContainer",
            "Tertiary", "OnTertiary", "TertiaryContainer", "OnTertiaryContainer",
            "Surface", "OnSurface", "SurfaceVariant", "OnSurfaceVariant",
            "SurfaceDim", "SurfaceBright", "SurfaceContainerLowest", "SurfaceContainerLow",
            "SurfaceContainer", "SurfaceContainerHigh", "SurfaceContainerHighest",
            "Outline", "OutlineVariant", "InverseSurface", "InverseOnSurface",
            "InversePrimary", "Scrim",
            "Error", "OnError", "ErrorContainer", "OnErrorContainer",
            "SuccessContainer", "OnSuccessContainer", "WarningContainer", "OnWarningContainer",
        };

        foreach (ThemeSeed seed in Enum.GetValues(typeof(ThemeSeed)))
        {
            foreach (var dark in new[] { false, true })
            {
                var palette = ThemePalettes.For(seed, dark);
                var keys = palette.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
                Assert.True(
                    expectedKeys.SetEquals(keys),
                    $"{seed}/{(dark ? "dark" : "light")}：缺 {string.Join(",", expectedKeys.Except(keys))}，多 {string.Join(",", keys.Except(expectedKeys))}");

                foreach (var (_, color) in palette)
                {
                    Assert.Equal((byte)0xFF, color.A);
                }
            }
        }
    }

    [Fact]
    public void ThemePalettes_DarkAndLightNeutralDiffer()
    {
        var light = ThemePalettes.For(ThemeSeed.Teal, dark: false)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var dark = ThemePalettes.For(ThemeSeed.Teal, dark: true)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        Assert.NotEqual(light["Surface"], dark["Surface"]);
        Assert.NotEqual(light["OnSurface"], dark["OnSurface"]);

        // P/S/T 随 seed：不同 seed 的 Primary 不同
        var teal = ThemePalettes.For(ThemeSeed.Teal, dark: true)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var indigo = ThemePalettes.For(ThemeSeed.Indigo, dark: true)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Assert.NotEqual(teal["Primary"], indigo["Primary"]);
    }

    // ===== 改名 Reload：数据目录迁移 =====

    [Fact]
    public void ResolveDefault_MigratesLegacyGameSaveSyncDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "gss-m45", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(baseDir, "localappdata");
        var legacy = Path.Combine(localAppData, "GameSaveSync");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "gamesavesync.db"), "legacy-data");

        var paths = AppPaths.ResolveDefault(baseDir, localAppData);

        // 新目录名 Reload，旧目录整体搬移
        Assert.EndsWith("Reload", paths.DataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(paths.DataRoot));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("legacy-data", File.ReadAllText(Path.Combine(paths.DataRoot, "gamesavesync.db")));

        Directory.Delete(baseDir, recursive: true);
    }

    [Fact]
    public void ResolveDefault_DoesNotOverwriteNewDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "gss-m45", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(baseDir, "localappdata");
        var legacy = Path.Combine(localAppData, "GameSaveSync");
        var modern = Path.Combine(localAppData, "Reload");
        Directory.CreateDirectory(Path.Combine(legacy, "old"));
        Directory.CreateDirectory(Path.Combine(modern, "new"));
        File.WriteAllText(Path.Combine(legacy, "old.txt"), "old");
        File.WriteAllText(Path.Combine(modern, "new.txt"), "new");

        var paths = AppPaths.ResolveDefault(baseDir, localAppData);

        // 新目录已存在：不迁移，旧目录原样保留
        Assert.True(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(modern, "new.txt")));
        Assert.False(File.Exists(Path.Combine(paths.DataRoot, "old.txt")));

        Directory.Delete(baseDir, recursive: true);
    }

    [Fact]
    public void AppPaths_Names()
    {
        Assert.Equal("Reload", AppPaths.AppName);
        Assert.Equal("GameSaveSync", AppPaths.LegacyAppName);
    }

    // ===== 全局排除 =====

    [Fact]
    public async Task GlobalExcludes_AreMergedIntoEveryRoot()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("GlobalExclude", ("keep.sav", TestEnvironment.Bytes(64, seed: 1)),
            ("junk.log", TestEnvironment.Bytes(32, seed: 2)));
        var game = await RegisterAsync(env, "全局排除", dir);

        // 全局排除 *.log：日志文件不进快照
        await env.Settings.SetAsync(SnapshotService.GlobalExcludeKey, "*.log", CancellationToken.None);

        var result = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, "global-exclude", CancellationToken.None);

        var snapshot = await env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.FileCount);   // 只有 keep.sav
    }

    [Fact]
    public async Task GlobalExcludes_CommentLinesAndBlanksAreIgnored()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir("GlobalExclude2", ("keep.sav", TestEnvironment.Bytes(64, seed: 3)),
            ("junk.tmp", TestEnvironment.Bytes(32, seed: 4)));
        var game = await RegisterAsync(env, "全局排除2", dir);

        await env.Settings.SetAsync(
            SnapshotService.GlobalExcludeKey,
            "# 注释行\n\n  \n*.tmp\n",
            CancellationToken.None);

        var result = await env.SnapshotService.CreateAsync(
            game.Id, SnapshotTrigger.Manual, null, CancellationToken.None);

        var snapshot = await env.Snapshots.GetAsync(result.SnapshotId!.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.FileCount);
    }

    private static async Task<Game> RegisterAsync(TestEnvironment env, string name, string dir)
    {
        var game = new Game
        {
            Name = name,
            Source = GameSource.Manual,
            CreatedAt = env.Clock.UtcNowMs,
            UpdatedAt = env.Clock.UtcNowMs,
        };
        game.SaveRoots.Add(new SaveRoot
        {
            Token = dir,
            IsTokenized = false,
            Include = [],
            Exclude = [],
            Recursive = true,
        });
        await env.Games.UpsertAsync(game, CancellationToken.None);
        return game;
    }
}
