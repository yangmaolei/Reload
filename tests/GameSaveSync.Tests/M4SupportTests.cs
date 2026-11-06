using System.IO.Compression;
using System.Text;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Probe;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Storage;
using GameSaveSync.Infrastructure;
using GameSaveSync.Tests.TestSupport;

namespace GameSaveSync.Tests;

/// <summary>M4 的探测模式、差异视图、诊断包、便携模式、封面缓存。</summary>
public sealed class M4SupportTests : IDisposable
{
    private readonly string _root;
    private readonly PhysicalFileSystem _fs = PhysicalFileSystem.Instance;
    private readonly InMemoryAppLogger _logger = new(500);

    public M4SupportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gss-m4s", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ProbeService_RanksChangedSaveDirectory()
    {
        var documents = Path.Combine(_root, "Documents");
        var saves = Path.Combine(documents, "My Games", "Probe Game", "Saves");
        Write(Path.Combine(saves, "slot1.sav"), TestEnvironment.Bytes(128, seed: 1));
        Write(Path.Combine(saves, "cache", "junk.tmp"), TestEnvironment.Bytes(64, seed: 2));

        var service = new ProbeService(_fs, new TestClock(), _logger);
        var options = new ProbeOptions { MaxDepth = 6 };
        var start = await service.StartAsync(new ProbeRoots(new[] { documents }), options, CancellationToken.None);

        Assert.True(start.FileCount >= 1);

        // 用户"玩了一会儿"：新增一个槽位、改了一个已有存档，另外装了个补丁
        Write(Path.Combine(saves, "slot2.sav"), TestEnvironment.Bytes(256, seed: 3));
        Write(Path.Combine(saves, "slot1.sav"), TestEnvironment.Bytes(512, seed: 4));
        Write(Path.Combine(documents, "My Games", "Probe Game", "patch.exe"), "patch");

        var result = await service.CompleteAsync(start, options, CancellationToken.None);

        var top = Assert.Single(result.Candidates);
        Assert.Equal(saves, top.Directory);
        Assert.Equal(1, top.NewFiles);
        Assert.Equal(1, top.ModifiedFiles);
        Assert.True(top.Score > 0);
        // 变化文件数包含 patch.exe（3 个），但候选目录里只有 Saves（cache 被跳过、exe 目录被过滤）
        Assert.Equal(3, result.ChangedFiles);
        Assert.Contains(top.Samples, s => s.Path.EndsWith("slot2.sav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProbeService_IgnoresBinaryPatchDirectories()
    {
        var local = Path.Combine(_root, "Local");
        var install = Path.Combine(local, "SomeGame", "bin");
        Write(Path.Combine(install, "game.dll"), TestEnvironment.Bytes(16, seed: 5));

        var service = new ProbeService(_fs, new TestClock(), _logger);
        var options = new ProbeOptions { MaxDepth = 6 };
        var start = await service.StartAsync(new ProbeRoots(new[] { local }), options, CancellationToken.None);

        Write(Path.Combine(install, "patch.dll"), TestEnvironment.Bytes(16, seed: 6));
        Write(Path.Combine(install, "patch2.dll"), TestEnvironment.Bytes(16, seed: 7));

        var result = await service.CompleteAsync(start, options, CancellationToken.None);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task SnapshotDiff_ComputesAddModifyRemove()
    {
        await using var env = TestEnvironment.Create(withWebDav: false);
        var dir = env.CreateSaveDir(
            "Saved Games/Diff",
            ("a.sav", TestEnvironment.Bytes(100, seed: 1)),
            ("b.sav", TestEnvironment.Bytes(200, seed: 2)));

        var game = await env.AddGameAsync("Diff Game", default, dir);

        var first = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "s1", CancellationToken.None);
        Assert.NotNull(first.SnapshotId);

        File.WriteAllBytes(Path.Combine(dir, "a.sav"), TestEnvironment.Bytes(300, seed: 3));
        File.Delete(Path.Combine(dir, "b.sav"));
        File.WriteAllBytes(Path.Combine(dir, "c.sav"), TestEnvironment.Bytes(50, seed: 4));

        var second = await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "s2", CancellationToken.None);
        Assert.NotNull(second.SnapshotId);

        var diff = await new SnapshotDiffService(env.Snapshots)
            .CompareWithPreviousAsync(second.SnapshotId!.Value, CancellationToken.None);

        Assert.True(diff.HasPrevious);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.ModifiedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Contains(diff.Entries, e => e.RelPath.EndsWith("c.sav") && e.Kind == DiffChangeKind.Added);
        Assert.Contains(diff.Entries, e => e.RelPath.EndsWith("a.sav") && e.Kind == DiffChangeKind.Modified);
        Assert.Contains(diff.Entries, e => e.RelPath.EndsWith("b.sav") && e.Kind == DiffChangeKind.Removed);
        Assert.NotEqual(0, diff.SizeDelta);
    }

    [Fact]
    public async Task Diagnostics_RedactsSecretsAndExportsZip()
    {
        await using var env = TestEnvironment.Create(withWebDav: true);
        var dir = env.CreateSaveDir("Saved Games/Diag", ("a.sav", TestEnvironment.Bytes(64, seed: 8)));
        var game = await env.AddGameAsync("Diag Game", default, dir);
        await env.SnapshotService.CreateAsync(game.Id, SnapshotTrigger.Manual, "diag", CancellationToken.None);

        await env.Settings.SetAsync(SettingsRepository.WebDavUrlKey, "https://alice:hunter2@dav.example.com/dav/", default);
        await env.Settings.SetAsync(SettingsRepository.WebDavUserKey, "alice", default);
        env.Logger.Info($"password=hunter2 用户目录 {env.ProfileDir} 数据目录 {env.Paths.DataRoot}");

        var diagnostics = new DiagnosticsService(
            env.Database, env.Objects, env.Snapshots, env.Outbox, env.RemoteSwitch, env.Settings,
            env.Logger, env.FileSystem, env.Paths, env.Clock, "4.0.0-test");

        var report = await diagnostics.BuildAsync(CancellationToken.None);
        Assert.Equal(1, report.Storage.Games);
        Assert.True(report.Storage.Snapshots >= 1);
        Assert.True(report.RemoteConfigured);

        var target = Path.Combine(_root, "diagnostics.zip");
        diagnostics.Export(report, target);
        Assert.True(File.Exists(target));

        string Read(string entryName)
        {
            using var archive = ZipFile.OpenRead(target);
            var entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var reportText = Read("report.txt");
        var logs = Read("logs.txt");
        var json = Read("diagnostics.json");

        foreach (var text in new[] { reportText, logs, json })
        {
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("alice:hunter2", text, StringComparison.Ordinal);
            Assert.DoesNotContain(env.ProfileDir, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("<DATA>", reportText.Replace("\r\n", "\n"), StringComparison.Ordinal);

        // 直接把 URL 丢给脱敏器：凭据段必须被吃掉，主机名保留
        var redacted = DiagnosticRedactor.CreateDefault(env.Paths, new[] { "alice" }, Array.Empty<string>())
            .Redact("https://alice:hunter2@dav.example.com/dav/ password=hunter2");
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("dav.example.com", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleSample_IsWrittenOnceAndParses()
    {
        var data = Path.Combine(_root, "rules-data");
        Directory.CreateDirectory(data);

        GameSaveSync.Core.Locator.SavePathRuleSet.WriteSampleIfMissing(_fs, data);
        var path = Path.Combine(data, GameSaveSync.Core.Locator.SavePathRuleSet.SampleFileName);
        Assert.True(File.Exists(path));

        var firstWrite = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        GameSaveSync.Core.Locator.SavePathRuleSet.WriteSampleIfMissing(_fs, data);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));

        var parsed = GameSaveSync.Core.Locator.SavePathRuleSet.Parse(File.ReadAllText(path));
        Assert.Contains(parsed.Rules, r => r.Id == "my-company");
        Assert.Contains(parsed.Rules, r => r.Id == "old-games");
    }

    [Fact]
    public void PortableMode_UsesProgramDirectoryWhenFlagExists()
    {
        var appDir = Path.Combine(_root, "app");
        var local = Path.Combine(_root, "local");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(local);

        var normal = AppPaths.ResolveDefault(appDir, local);
        Assert.False(normal.IsPortable);
        Assert.Equal(Path.Combine(local, "Reload"), normal.DataRoot);

        File.WriteAllText(Path.Combine(appDir, AppPaths.PortableFlagName), "portable");
        var portable = AppPaths.ResolveDefault(appDir, local);
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(appDir, "data"), portable.DataRoot);
        Assert.False(portable.DatabasePath.Contains(local, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CoverService_UsesCacheAndSteamCdnUri()
    {
        var paths = new AppPaths(Path.Combine(_root, "coverdata"));
        paths.EnsureCreated();

        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/480/header.jpg",
            CoverService.BuildSteamCoverUri("480")!.ToString());
        Assert.Null(CoverService.BuildSteamCoverUri("not-a-number"));

        var game = new Game { Name = "Spacewar", Source = GameSource.Steam, ExternalId = "480" };
        var cachedPath = Path.Combine(paths.CoversDir, "steam-480-s.jpg");
        File.WriteAllBytes(cachedPath, TestEnvironment.Bytes(32, seed: 9));

        var service = new CoverService(paths, _fs, downloader: null, _logger);
        Assert.Equal(cachedPath, await service.GetAsync(game, PosterKind.Square, CancellationToken.None));

        var game2 = new Game { Name = "Other", Source = GameSource.Steam, ExternalId = "570" };
        var downloader = new FakeDownloader(TestEnvironment.Bytes(64, seed: 10));
        var service2 = new CoverService(paths, _fs, downloader, _logger);
        var downloaded = await service2.GetAsync(game2, PosterKind.Portrait, CancellationToken.None);

        Assert.NotNull(downloaded);
        Assert.True(File.Exists(downloaded));
        Assert.Equal(1, downloader.Calls);
        Assert.Equal(64, new FileInfo(downloaded!).Length);
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

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Write(string path, string content) =>
        Write(path, Encoding.UTF8.GetBytes(content));

    private sealed class FakeDownloader : ICoverDownloader
    {
        private readonly byte[] _content;

        public FakeDownloader(byte[] content) => _content = content;

        public int Calls { get; private set; }

        public Task<byte[]?> DownloadAsync(Uri uri, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<byte[]?>(_content);
        }
    }
}
