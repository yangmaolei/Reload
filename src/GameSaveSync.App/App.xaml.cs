using System.Windows;
using System.Windows.Threading;
using GameSaveSync.App.Services;
using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Probe;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Detection;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Hashing;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Retention;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Core.Security;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;
using GameSaveSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace GameSaveSync.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Reload.SingleInstance";
    private const string ActivateEventName = @"Local\GameSaveSync.Activate";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _lifetime;
    private ServiceProvider? _services;
    private TrayIconService? _tray;
    private bool _exiting;

    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!isFirstInstance)
        {
            // 第二个实例：唤起已有窗口后退出
            _activateEvent.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            var logger = _services?.GetService<IAppLogger>();
            logger?.Error("未处理的 UI 异常", args.Exception);
            MessageBox.Show($"发生未处理的错误：{args.Exception.Message}", "Reload", MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _services?.GetService<IAppLogger>()?.Error("未处理的域异常", args.ExceptionObject as Exception);

        try
        {
            _lifetime = new CancellationTokenSource();
            _services = BuildServices();

            var database = _services.GetRequiredService<SqliteDatabase>();
            database.Initialize();

            var appPaths = _services.GetRequiredService<AppPaths>();
            SavePathRuleSet.WriteSampleIfMissing(_services.GetRequiredService<IFileSystem>(), appPaths.DataRoot);

            var objectStore = _services.GetRequiredService<IObjectStore>();
            var cleaned = objectStore.CleanupTemporaryFiles();

            var logger = _services.GetRequiredService<IAppLogger>();
            var startupPaths = _services.GetRequiredService<AppPaths>();
            logger.Info($"Reload 启动（M5），清理崩溃残留临时对象 {cleaned} 个，数据目录 {(startupPaths.IsPortable ? "(便携) " : string.Empty)}{startupPaths.DataRoot}");

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            logger.Info($"主窗口 Left={window.Left:0} Top={window.Top:0} {window.Width:0}x{window.Height:0}，" +
                        $"工作区 {SystemParameters.WorkArea}，屏幕 {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}");

            _tray = new TrayIconService(
                _services.GetRequiredService<SnapshotScheduler>(),
                _services.GetRequiredService<AutoStartService>(),
                logger,
                ShowMainWindow,
                ExitApplication);
            _tray.Initialize();

            // 主题：从设置读取 seed/模式并注入颜色字典（DynamicResource 全局生效）
            _services.GetRequiredService<ThemeService>().Install(this);

            StartBackgroundServices(_services, logger);
            StartActivationListener();
            ScheduleFirstRunWizard(_services, logger);
            OpenDialogForTesting();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex}", "Reload", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// 测试钩子：设置 <c>RELOAD_OPEN_DIALOG</c> 环境变量可直接打开指定弹窗，
    /// 用于 UI 回归验证（圆角/拖拽/文字清晰度），避免自动化脚本依赖屏幕坐标。
    /// 可选值：wizard（扫描游戏向导）。
    /// </summary>
    private void OpenDialogForTesting()
    {
        var target = Environment.GetEnvironmentVariable("RELOAD_OPEN_DIALOG");
        if (string.IsNullOrWhiteSpace(target) || _services is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (target.Equals("wizard", StringComparison.OrdinalIgnoreCase))
                {
                    var vm = _services.GetRequiredService<GameSaveSync.App.ViewModels.WizardViewModel>();
                    var dialog = new GameSaveSync.App.Views.WizardWindow(vm, startStep: 1)
                    {
                        Owner = MainWindow,
                    };
                    dialog.Show();
                }
            }
            catch (Exception ex)
            {
                _services.GetRequiredService<IAppLogger>()
                    .Warn($"测试钩子打开弹窗失败：{ex.Message}");
            }
        }));
    }

    /// <summary>后台三件事：WebDAV 配置装配、outbox 轮询、调度器（进程/文件/idle 触发）与启动补扫。</summary>
    private void StartBackgroundServices(IServiceProvider services, IAppLogger logger)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;

        var outbox = services.GetRequiredService<OutboxProcessor>();
        outbox.PermanentlyFailed += failure =>
        {
            var text = $"{failure.Item.Op} 连续失败，已停止重试：{failure.Error}";
            _tray?.ShowBalloon("自动上传失败", text, System.Windows.Forms.ToolTipIcon.Warning);
            logger.Error(text);
        };

        _ = Task.Run(async () =>
        {
            try
            {
                // 启动重放：inflight 视为未完成，pending 提前到现在
                var store = services.GetRequiredService<IOutboxStore>();
                var requeued = await store.RequeueInflightAsync(token).ConfigureAwait(false);
                if (requeued > 0)
                {
                    logger.Info($"启动重放 {requeued} 个未完成的 outbox 项");
                }

                await outbox.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                logger.Error("outbox 后台循环退出", ex);
            }
        }, token);

        _ = Task.Run(async () =>
        {
            try
            {
                var webDav = services.GetRequiredService<WebDavSettingsService>();
                var options = await webDav.LoadAsync(token).ConfigureAwait(false);
                await webDav.ApplyAsync(options, token).ConfigureAwait(false);
                logger.Info(options.IsConfigured
                    ? $"WebDAV 已就绪：{options.BaseUrl} / {options.RemoteRoot}"
                    : "未配置 WebDAV，快照只保存在本地");
            }
            catch (Exception ex)
            {
                logger.Warn("初始化 WebDAV 配置失败", ex);
            }
        }, token);

        _ = Task.Run(async () =>
        {
            try
            {
                // M3：先注册本机设备，再启动 60s 下行轮询
                var index = services.GetRequiredService<IRemoteIndexService>();
                var poller = services.GetRequiredService<RemoteIndexPoller>();
                poller.Changed += result =>
                {
                    if (result.ImportedSnapshots > 0)
                    {
                        _tray?.ShowBalloon("收到新快照", $"从其他设备同步了 {result.ImportedSnapshots} 个快照。");
                    }
                };

                await index.RefreshDevicesAsync(token).ConfigureAwait(false);
                await poller.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                logger.Warn("下行轮询启动失败", ex);
            }
        }, token);

        _ = Task.Run(async () =>
        {
            try
            {
                var scheduler = services.GetRequiredService<SnapshotScheduler>();
                await scheduler.StartAsync(token).ConfigureAwait(false);
                _tray?.UpdateStatus("自动备份运行中");

                scheduler.Failed += (gameId, what, ex) =>
                    _tray?.ShowBalloon("自动备份失败", $"{what}（游戏 {gameId:N}）：{ex?.Message}",
                        System.Windows.Forms.ToolTipIcon.Warning);

                // 启动补扫：应用未运行期间的变更
                var created = await scheduler.RunStartupCatchUpAsync(token).ConfigureAwait(false);
                if (created > 0)
                {
                    _tray?.ShowBalloon("已补备份", $"发现应用未运行期间的变化，补出 {created} 个快照。");
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                logger.Error("调度器启动失败", ex);
            }
        }, token);
    }

    /// <summary>首次启动（没有游戏、也没配远端）时在空闲时弹出三步向导。</summary>
    private void ScheduleFirstRunWizard(IServiceProvider services, IAppLogger logger)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var games = await services.GetRequiredService<IGameRepository>()
                    .ListAsync(CancellationToken.None).ConfigureAwait(true);
                if (games.Count > 0)
                {
                    return;
                }

                var webDav = await services.GetRequiredService<WebDavSettingsService>()
                    .LoadAsync(CancellationToken.None).ConfigureAwait(true);
                if (webDav.IsConfigured)
                {
                    return;
                }

                var added = services.GetRequiredService<IDialogService>().ShowWizard(0);
                var main = services.GetRequiredService<MainViewModel>();
                await main.LoadAsync().ConfigureAwait(true);

                if (added > 0)
                {
                    main.SelectedGame = main.Games.FirstOrDefault();
                    main.StatusMessage = $"欢迎：已从平台库添加 {added} 个游戏";
                }

                logger.Info($"首次向导结束：新增 {added} 个游戏");
            }
            catch (Exception ex)
            {
                logger.Error("首次向导失败", ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void StartActivationListener()
    {
        var handle = _activateEvent;
        if (handle is null)
        {
            return;
        }

        var token = _lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { handle };
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var index = WaitHandle.WaitAny(handles, 500);
                    if (index == 0)
                    {
                        Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void MinimizeToTray()
    {
        MainWindow?.Hide();
        _tray?.ShowMinimizedHint();
    }

    private void ExitApplication()
    {
        var pending = _services?.GetService<IOutboxStore>()?.CountPendingAsync(CancellationToken.None)
            .GetAwaiter().GetResult() ?? 0;

        if (pending > 0)
        {
            var answer = MessageBox.Show(
                $"还有 {pending} 项待上传，退出后会在下次启动时继续。确定要退出吗？",
                "Reload",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _exiting = true;
        Shutdown();
    }

    private static ServiceProvider BuildServices()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "reload-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton<IAppLogger>(new CompositeAppLogger(Log.Logger));
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IFileSystem>(PhysicalFileSystem.Instance);
        services.AddSingleton<IHashService>(Blake3HashService.Instance);
        services.AddSingleton<ILocatorEnvironment, WindowsLocatorEnvironment>();
        services.AddSingleton<IPathTokenResolver>(sp =>
        {
            var tokens = PathTokenResolver.BuildDefaultTokens();
            foreach (var (key, value) in sp.GetRequiredService<ILocatorEnvironment>().GetExtraPathTokens())
            {
                tokens[key] = value;
            }

            return new PathTokenResolver(tokens);
        });
        services.AddSingleton<ICredentialProtector>(DpapiCredentialProtector.Instance);
        services.AddSingleton<IFileIdentityProvider>(Win32FileIdentityProvider.Instance);

        services.AddSingleton<SqliteDatabase>(_ => new SqliteDatabase(paths.DatabasePath));
        services.AddSingleton<IGameRepository, GameRepository>();
        services.AddSingleton<ISnapshotRepository, SnapshotRepository>();
        services.AddSingleton<IObjectRepository, ObjectRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IFingerprintStore, FingerprintStore>();
        services.AddSingleton<IOutboxStore, SqliteOutboxStore>();
        services.AddSingleton<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<IIndexStateRepository, IndexStateRepository>();

        services.AddSingleton<IObjectStore, FileSystemObjectStore>();
        services.AddSingleton<ISnapshotManifestStore, SnapshotManifestStore>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<IRestoreService, RestoreService>();

        services.AddSingleton<ConfigurableRemoteStore>();
        services.AddSingleton<IRemoteStore>(sp => sp.GetRequiredService<ConfigurableRemoteStore>());
        services.AddSingleton<WebDavRemoteStoreFactory>();
        services.AddSingleton<WebDavSettingsService>();

        services.AddSingleton<IRemoteSnapshotImporter, RemoteSnapshotImporter>();
        services.AddSingleton<IRemoteIndexService, RemoteIndexService>();
        services.AddSingleton<IConflictService, ConflictService>();
        services.AddSingleton<ISnapshotExporter, SnapshotExporter>();
        services.AddSingleton<RemoteIndexPoller>();

        services.AddSingleton<IRemoteOperationExecutor, RemoteOperationExecutor>();
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<IUploadService, UploadService>();
        services.AddSingleton<IRetentionService, RetentionService>();
        services.AddSingleton<SnapshotDiffService>();

        services.AddSingleton(sp => GameDiscoveryService.LoadRules(
            sp.GetRequiredService<IFileSystem>(),
            paths,
            sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<GameDiscoveryService>();
        services.AddSingleton<ProbeService>();
        services.AddSingleton<ICoverDownloader, HttpCoverDownloader>();
        services.AddSingleton<GameSaveSync.Core.Online.IOnlineSavePathProvider, GameSaveSync.Core.Online.PcGamingWikiProvider>();
        services.AddSingleton<CoverService>();
        services.AddSingleton(sp => new DiagnosticsService(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IObjectRepository>(),
            sp.GetRequiredService<ISnapshotRepository>(),
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<IRemoteStore>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<IAppLogger>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<IClock>(),
            typeof(App).Assembly.GetName().Version?.ToString()));

        services.AddSingleton<IProcessWatcher>(sp =>
            new WindowsProcessWatcher(sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<IFileChangeWatcher>(sp => new SaveFolderWatcher(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathTokenResolver>(),
            sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<IStabilityGate>(sp => new StabilityGate(
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathTokenResolver>()));
        services.AddSingleton<IResourceProbe>(sp => new WindowsResourceProbe(sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<IBackgroundIoScope, ThreadBackgroundIoScope>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<SnapshotScheduler>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddTransient<WizardViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _exiting = true;
            _lifetime?.Cancel();

            _services?.GetService<SnapshotScheduler>()?.StopAsync().GetAwaiter().GetResult();
            _tray?.Dispose();
            _services?.Dispose();
            Log.CloseAndFlush();
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // 退出路径尽量不抛
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _activateEvent?.Dispose();
            _lifetime?.Dispose();
        }

        base.OnExit(e);
    }
}
