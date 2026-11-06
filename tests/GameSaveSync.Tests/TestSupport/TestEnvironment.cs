using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Hashing;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Detection;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Retention;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;
using GameSaveSync.Infrastructure;
using GameSaveSync.WebDav;
using GameSaveSync.WebDav.Internal;
using Microsoft.Data.Sqlite;

namespace GameSaveSync.Tests.TestSupport;

/// <summary>
/// 一套完整的临时运行环境：临时数据目录 + 临时"用户目录"（令牌映射）+ 进程内 WebDAV。
/// 所有测试都跑在真实文件系统上（含长路径），只有 WebDAV 是进程内模拟。
/// </summary>
public sealed class TestEnvironment : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = new();
    private bool _ownsServer;
    private bool _disposed;

    private TestEnvironment(
        string root,
        bool withWebDav,
        SnapshotSchedulerOptions? schedulerOptions,
        InMemoryWebDavServer? sharedServer,
        IReadOnlyDictionary<string, string>? extraTokens)
    {
        Root = root;
        DataRoot = Path.Combine(root, "data");
        ProfileDir = Path.Combine(root, "profile");
        SaveGamesDir = Path.Combine(ProfileDir, "Saved Games");

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ProfileDir);
        Directory.CreateDirectory(SaveGamesDir);

        var tokenMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PathTokenResolver.UserProfile] = ProfileDir,
            [PathTokenResolver.AppData] = Path.Combine(ProfileDir, "AppData", "Roaming"),
            [PathTokenResolver.LocalAppData] = Path.Combine(ProfileDir, "AppData", "Local"),
            [PathTokenResolver.Documents] = Path.Combine(ProfileDir, "Documents"),
            [PathTokenResolver.SavedGames] = SaveGamesDir,
        };

        if (extraTokens is not null)
        {
            foreach (var (key, value) in extraTokens)
            {
                tokenMap[key] = value;
            }
        }

        Tokens = new PathTokenResolver(tokenMap);

        Paths = new AppPaths(DataRoot);
        Paths.EnsureCreated();

        FileSystem = PhysicalFileSystem.Instance;
        Logger = new InMemoryAppLogger(500);
        Database = new SqliteDatabase(Paths.DatabasePath);
        Database.Initialize();

        Games = new GameRepository(Database);
        Snapshots = new SnapshotRepository(Database);
        Objects = new ObjectRepository(Database);
        Settings = new SettingsRepository(Database);
        Store = new FileSystemObjectStore(Paths, FileSystem, Blake3HashService.Instance);
        ManifestStore = new SnapshotManifestStore(Paths, Snapshots, Games, FileSystem, Logger);

        Fingerprints = new FingerprintStore(Database);
        FileIdentity = TryCreateFileIdentityProvider();

        SnapshotService = new SnapshotService(
            Games, Snapshots, Objects, Store, FileSystem, Tokens, Settings, ManifestStore, Logger,
            Clock, Fingerprints, FileIdentity, Blake3HashService.Instance);

        RestoreService = new RestoreService(
            Games, Snapshots, Store, FileSystem, Blake3HashService.Instance, Tokens, SnapshotService, Logger, Settings);

        if (withWebDav)
        {
            Server = sharedServer ?? new InMemoryWebDavServer();
            _ownsServer = sharedServer is null;
            RemoteOptions = new WebDavOptions
            {
                BaseUrl = Server.BaseUrl,
                RemoteRoot = "GameSaveSync",
                Username = "tester",
                Password = "secret",
                TimeoutSeconds = 30,
                UseProxy = false,
            };
            Remote = new WebDavRemoteStore(RemoteOptions, Logger);
        }
        else
        {
            Server = null;
            RemoteOptions = new WebDavOptions();
            Remote = null;
        }

        RemoteSwitch = new SwitchingRemoteStore(Remote ?? (IRemoteStore)NullRemoteStore.Instance);
        IRemoteStore remoteStore = RemoteSwitch;

        Outbox = new SqliteOutboxStore(Database);
        Devices = new DeviceRepository(Database, Settings, Clock);
        IndexState = new IndexStateRepository(Database);
        Importer = new RemoteSnapshotImporter(
            remoteStore, Snapshots, Games, Store, Objects, ManifestStore, Blake3HashService.Instance, Logger);
        IndexService = new RemoteIndexService(
            remoteStore, Devices, IndexState, Snapshots, Games, Importer, Clock, Logger);
        Poller = new RemoteIndexPoller(IndexService, Logger, TimeSpan.FromMilliseconds(250));
        Conflicts = new ConflictService(IndexState, Snapshots, Games, IndexService, Devices, Logger);

        Executor = new RemoteOperationExecutor(
            remoteStore, Store, Objects, Snapshots, ManifestStore, Settings, IndexService, Logger);
        OutboxProcessor = new OutboxProcessor(Outbox, Executor, Clock, Logger);
        UploadService = new UploadService(Snapshots, Objects, Store, remoteStore, Outbox, OutboxProcessor, Logger);
        Retention = new RetentionService(Games, Snapshots, Objects, Store, ManifestStore, Outbox, Clock, Logger);

        ProcessWatcher = new FakeProcessWatcher();
        FileWatcher = new FakeFileChangeWatcher();
        ResourceProbe = new FakeResourceProbe();
        BackgroundIo = new RecordingBackgroundIoScope();
        StabilityGate = new StabilityGate(FileSystem, Tokens, new StabilityOptions
        {
            RecheckDelay = TimeSpan.FromMilliseconds(60),
            RetryDelays = new[] { TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80) },
        });

        SchedulerOptions = schedulerOptions ?? new SnapshotSchedulerOptions
        {
            ExitDelay = TimeSpan.FromMilliseconds(120),
            DebounceDelay = TimeSpan.FromMilliseconds(150),
            IdleInterval = TimeSpan.FromMilliseconds(300),
            StartupCatchUpDelay = TimeSpan.Zero,
            RunRetentionAfterSnapshot = false,
        };

        Scheduler = new SnapshotScheduler(
            Games, Snapshots, SnapshotService, UploadService, Retention,
            ProcessWatcher, FileWatcher, StabilityGate, ResourceProbe, BackgroundIo, Logger, SchedulerOptions);
    }

    private static IFileIdentityProvider? TryCreateFileIdentityProvider()
    {
        try
        {
            return Win32FileIdentityProvider.Instance;
        }
        catch
        {
            return null;
        }
    }

    public TestClock Clock { get; } = new();

    public string Root { get; }

    public string DataRoot { get; }

    public string ProfileDir { get; }

    public string SaveGamesDir { get; }

    public PathTokenResolver Tokens { get; }

    public AppPaths Paths { get; }

    public IFileSystem FileSystem { get; }

    public InMemoryAppLogger Logger { get; }

    public SqliteDatabase Database { get; }

    public GameRepository Games { get; }

    public SnapshotRepository Snapshots { get; }

    public ObjectRepository Objects { get; }

    public SettingsRepository Settings { get; }

    public IObjectStore Store { get; }

    public SnapshotManifestStore ManifestStore { get; }

    public SnapshotService SnapshotService { get; }

    public RestoreService RestoreService { get; }

    public UploadService UploadService { get; }

    public FingerprintStore Fingerprints { get; }

    public IFileIdentityProvider? FileIdentity { get; }

    public SqliteOutboxStore Outbox { get; }

    public DeviceRepository Devices { get; }

    public IndexStateRepository IndexState { get; }

    public RemoteSnapshotImporter Importer { get; }

    public RemoteIndexService IndexService { get; }

    public RemoteIndexPoller Poller { get; }

    public ConflictService Conflicts { get; }

    /// <summary>测试用：可切换"断网/联网"而不重建服务图。</summary>
    public SwitchingRemoteStore RemoteSwitch { get; }

    /// <summary>模拟拔网线：指向一个没有人监听的端口，请求会以连接失败告终。</summary>
    public void GoOffline()
    {
        var port = GetFreeTcpPort();
        RemoteSwitch.SetInner(new WebDavRemoteStore(
            new WebDavOptions
            {
                BaseUrl = $"http://127.0.0.1:{port}/dav/",
                RemoteRoot = "GameSaveSync",
                TimeoutSeconds = 2,
                UseProxy = false,
            },
            Logger));
    }

    public void GoOnline()
    {
        if (Remote is not null)
        {
            RemoteSwitch.SetInner(Remote);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public RemoteOperationExecutor Executor { get; }

    public OutboxProcessor OutboxProcessor { get; }

    public RetentionService Retention { get; }

    public FakeProcessWatcher ProcessWatcher { get; }

    public FakeFileChangeWatcher FileWatcher { get; }

    public FakeResourceProbe ResourceProbe { get; }

    public RecordingBackgroundIoScope BackgroundIo { get; }

    public StabilityGate StabilityGate { get; }

    public SnapshotSchedulerOptions SchedulerOptions { get; }

    public SnapshotScheduler Scheduler { get; }

    public InMemoryWebDavServer? Server { get; }

    public WebDavOptions RemoteOptions { get; }

    public WebDavRemoteStore? Remote { get; }

    public IUploadService Uploads => UploadService;

    public static TestEnvironment Create(
        bool withWebDav = true,
        SnapshotSchedulerOptions? schedulerOptions = null,
        InMemoryWebDavServer? sharedServer = null,
        IReadOnlyDictionary<string, string>? extraTokens = null) =>
        new(
            Path.Combine(Path.GetTempPath(), "gss-tests", Guid.NewGuid().ToString("N")),
            withWebDav,
            schedulerOptions,
            sharedServer,
            extraTokens);

    /// <summary>模拟"第二台机器"：独立数据目录/用户目录，但连同一个 WebDAV。</summary>
    public static TestEnvironment CreateSecondDevice(InMemoryWebDavServer server) =>
        Create(withWebDav: true, schedulerOptions: null, sharedServer: server);

    /// <summary>把一个绝对目录（或令牌路径）建成游戏的存档根。</summary>
    public SaveRoot MakeSaveRoot(string absoluteOrTokenizedPath, params string[] excludes)
    {
        var root = SaveRootResolver.CreateSaveRoot(absoluteOrTokenizedPath, Tokens);
        root.Exclude.AddRange(excludes);
        return root;
    }

    public SaveRoot MakeSaveRootWithIncludes(string absoluteOrTokenizedPath, string[] includes, string[] excludes)
    {
        var root = SaveRootResolver.CreateSaveRoot(absoluteOrTokenizedPath, Tokens);
        root.Include.AddRange(includes);
        root.Exclude.AddRange(excludes);
        return root;
    }

    public async Task<Game> AddGameAsync(string name, CancellationToken ct = default, params string[] saveDirs)
    {
        var game = new Game
        {
            Name = name,
            Source = GameSource.Manual,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        foreach (var dir in saveDirs)
        {
            game.SaveRoots.Add(MakeSaveRoot(dir));
        }

        await Games.UpsertAsync(game, ct).ConfigureAwait(false);
        return game;
    }

    /// <summary>建一个含若干文件的目录（内容可指定），返回目录绝对路径。</summary>
    public string CreateSaveDir(string relativeUnderProfile = "Saved Games/DemoGame", params (string Path, byte[] Content)[] files)
    {
        var dir = Path.Combine(ProfileDir, relativeUnderProfile.Replace('/', '\\'));
        Directory.CreateDirectory(dir);

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(dir, relative.Replace('/', '\\'));
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(full, content);
        }

        return dir;
    }

    public static byte[] Bytes(int count, int seed = 0)
    {
        var buffer = new byte[count];
        for (var i = 0; i < count; i++)
        {
            buffer[i] = (byte)((i * 31 + seed) % 251);
        }

        return buffer;
    }

    /// <summary>目录内容的 SHA/BLAKE3 清单（相对路径 → 哈希），用于逐字节比对。</summary>
    public Dictionary<string, string> HashTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in FileSystem.EnumerateFiles(root, recursive: true))
        {
            using var stream = FileSystem.OpenRead(file);
            var hasher = Blake3HashService.Instance.CreateHasher();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Update(buffer.AsSpan(0, read));
            }

            result[FileSystem.GetRelativePath(root, file)] = hasher.Finalize();
        }

        return result;
    }

    /// <summary>逐文件比对两棵树（相对路径 → 哈希）。</summary>
    public static void AssertTreesEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (relative, hash) in expected)
        {
            Assert.True(actual.TryGetValue(relative, out var actualHash), $"缺少文件：{relative}");
            Assert.Equal(hash, actualHash);
        }
    }

    public static string HashFile(string path) => HashFileWith(File.ReadAllBytes(path));

    public static string HashFileWith(byte[] content) => Blake3HashService.Instance.HashBytes(content);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Remote?.Dispose();
        if (Server is not null && _ownsServer)
        {
            await Server.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                break;
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                ClearReadOnly(Root);
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // ignore
            }
        }
    }
}

internal static class WebDavTestExtensions
{
    public static WebDavOptions WithServer(this WebDavOptions options, InMemoryWebDavServer server) => new()
    {
        BaseUrl = server.BaseUrl,
        RemoteRoot = options.RemoteRoot,
        Username = options.Username,
        Password = options.Password,
        TimeoutSeconds = options.TimeoutSeconds,
    };
}
