using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Online;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Restore;
using GameSaveSync.App.Views;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Retention;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.App.ViewModels;

public enum AppPage
{
    Games = 0,
    Diagnostics = 1,
    Settings = 2,
}

/// <summary>导航栏一项。</summary>
public sealed record NavEntry(AppPage Page, string Glyph, string Label);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IGameRepository _games;
    private readonly ISnapshotRepository _snapshots;
    private readonly ISnapshotService _snapshotService;
    private readonly IRestoreService _restoreService;
    private readonly IUploadService _uploadService;
    private readonly IRetentionService _retention;
    private readonly IIndexStateRepository _indexState;
    private readonly IConflictService _conflicts;
    private readonly ISnapshotExporter _exporter;
    private readonly SnapshotDiffService _diffs;
    private readonly CoverService _covers;
    private readonly SavePathRefreshService _refreshPaths;
    private readonly IPathTokenResolver _tokens;
    private readonly IFileSystem _fileSystem;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;

    public MainViewModel(
        IGameRepository games,
        ISnapshotRepository snapshots,
        ISnapshotService snapshotService,
        IRestoreService restoreService,
        IUploadService uploadService,
        IRetentionService retention,
        IIndexStateRepository indexState,
        IConflictService conflicts,
        ISnapshotExporter exporter,
        SnapshotDiffService diffs,
        CoverService covers,
        SavePathRefreshService refreshPaths,
        IPathTokenResolver tokens,
        IFileSystem fileSystem,
        IDialogService dialogs,
        IAppLogger logger,
        IClock clock,
        SettingsViewModel settings,
        DiagnosticsViewModel diagnostics,
        LogViewModel log)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        _uploadService = uploadService ?? throw new ArgumentNullException(nameof(uploadService));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _indexState = indexState ?? throw new ArgumentNullException(nameof(indexState));
        _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _diffs = diffs ?? throw new ArgumentNullException(nameof(diffs));
        _covers = covers ?? throw new ArgumentNullException(nameof(covers));
        _refreshPaths = refreshPaths ?? throw new ArgumentNullException(nameof(refreshPaths));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public SettingsViewModel Settings { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public LogViewModel Log { get; }

    public ObservableCollection<GameListItemViewModel> Games { get; } = new();

    /// <summary>过搜索词之后的列表（卡片墙绑这个）。</summary>
    public ObservableCollection<GameListItemViewModel> VisibleGames { get; } = new();

    public IReadOnlyList<NavEntry> Navigation { get; } = new[]
    {
        new NavEntry(AppPage.Games, "\uE7FC", "游戏"),
        new NavEntry(AppPage.Diagnostics, "\uE9D9", "诊断"),
        new NavEntry(AppPage.Settings, "\uE713", "设置"),
    };

    public ObservableCollection<SnapshotListItemViewModel> Snapshots { get; } = new();

    [ObservableProperty]
    private AppPage currentPage =
        string.Equals(Environment.GetEnvironmentVariable("RELOAD_START_PAGE"), "settings", StringComparison.OrdinalIgnoreCase)
            ? AppPage.Settings
            : AppPage.Games;

    public int CurrentPageIndex
    {
        get => (int)CurrentPage;
        set
        {
            var page = (AppPage)Math.Clamp(value, 0, 2);
            if (CurrentPage == page)
            {
                return;
            }

            CurrentPage = page;
            OnPropertyChanged();
        }
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsGamesPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(CurrentPageIndex));

        if (value == AppPage.Diagnostics)
        {
            _ = Diagnostics.LoadAsync();
        }
    }

    public bool IsGamesPage => CurrentPage == AppPage.Games;

    public bool IsDiagnosticsPage => CurrentPage == AppPage.Diagnostics;

    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    [ObservableProperty]
    private GameListItemViewModel? selectedGame;

    [ObservableProperty]
    private SnapshotListItemViewModel? selectedSnapshot;

    [ObservableProperty]
    private SnapshotDiff? diff;

    [ObservableProperty]
    private string diffSummary = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string gamesSummary = "还没有游戏";

    [ObservableProperty]
    private bool isBusy;

    /// <summary>「刷新存档路径」进行中（用于禁用按钮，防重复请求）。</summary>
    [ObservableProperty]
    private bool isRefreshingSavePaths;

    /// <summary>「刷新存档路径」的状态提示（加载中 / 成功 / 失败原因）。</summary>
    [ObservableProperty]
    private string refreshSavePathsStatus = string.Empty;

    public bool HasSelection => SelectedGame is not null;

    public bool IsDetailVisible => SelectedGame is not null;

    public bool IsWallVisible => SelectedGame is null;

    partial void OnSelectedGameChanged(GameListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsDetailVisible));
        OnPropertyChanged(nameof(IsWallVisible));
        _ = ReloadSnapshotsAsync();
    }

    partial void OnSelectedSnapshotChanged(SnapshotListItemViewModel? value)
    {
        _ = LoadDiffAsync(value);
    }

    private CancellationTokenSource? _statusCts;

    /// <summary>Snackbar 自动消失：10 秒后清空，避免长期盖住页面内容。</summary>
    partial void OnStatusMessageChanged(string value)
    {
        _statusCts?.Cancel();

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _statusCts = cts;
        _ = ClearStatusLaterAsync(value, cts);
    }

    private async Task ClearStatusLaterAsync(string shown, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(true);
            if (string.Equals(StatusMessage, shown, StringComparison.Ordinal))
            {
                StatusMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的状态消息替换
        }
        finally
        {
            if (ReferenceEquals(_statusCts, cts))
            {
                _statusCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var selectedId = SelectedGame?.Id;
        var stored = await _games.ListAsync(CancellationToken.None).ConfigureAwait(true);

        // 旧条目要退订，避免重建后事件重复触发
        foreach (var old in Games)
        {
            old.PosterKindChanged -= SavePosterPreference;
        }

        Games.Clear();
        foreach (var game in stored)
        {
            var item = new GameListItemViewModel(game);
            await item.RefreshAsync(_snapshots, _indexState, CancellationToken.None).ConfigureAwait(true);

            // 先用缓存里的海报立刻显示，缺哪种形态后台再补下
            var cached = new Dictionary<PosterKind, string>();
            foreach (var kind in PosterKinds.All)
            {
                var path = _covers.GetCachedPath(game, kind);
                if (path is not null)
                {
                    cached[kind] = path;
                }
            }

            // 升级前的旧缓存（不分形态）也先用上，避免老用户的海报凭空消失
            if (cached.Count == 0)
            {
                var legacy = _covers.GetLegacyCachedPath(game);
                if (legacy is not null)
                {
                    cached[_covers.ClassifyLegacyCached(game)] = legacy;
                }
            }

            if (cached.Count > 0)
            {
                item.ApplyPosters(cached, ResolvePreferredPoster(game));
            }

            // 用户切换海报形态时把偏好写回数据库
            item.PosterKindChanged += SavePosterPreference;

            Games.Add(item);
            QueueCoverLoad(item);
        }

        ApplyFilter();
        SelectedGame = Games.FirstOrDefault(g => g.Id == selectedId);
        await ReloadSnapshotsAsync().ConfigureAwait(true);
        StatusMessage = Games.Count == 0 ? "还没有游戏，点「扫描游戏」或「手动添加」开始" : $"共 {Games.Count} 个游戏";
        await Diagnostics.LoadAsync().ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        VisibleGames.Clear();

        foreach (var game in Games)
        {
            if (query.Length == 0 ||
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                game.RootSummary.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleGames.Add(game);
            }
        }

        GamesSummary = Games.Count == 0
            ? "还没有游戏"
            : query.Length == 0
                ? $"共 {Games.Count} 个游戏"
                : $"{VisibleGames.Count} / {Games.Count} 个游戏";
    }

    [RelayCommand]
    private async Task ReloadSnapshotsAsync()
    {
        Snapshots.Clear();
        Diff = null;
        DiffSummary = string.Empty;

        if (SelectedGame is null)
        {
            SelectedSnapshot = null;
            return;
        }

        var list = await _snapshots.ListByGameAsync(SelectedGame.Id, CancellationToken.None).ConfigureAwait(true);
        foreach (var snapshot in list)
        {
            Snapshots.Add(new SnapshotListItemViewModel(snapshot));
        }

        SelectedSnapshot = Snapshots.FirstOrDefault();
    }

    private async Task LoadDiffAsync(SnapshotListItemViewModel? item)
    {
        if (item is null || SelectedGame is null)
        {
            Diff = null;
            DiffSummary = string.Empty;
            return;
        }

        try
        {
            var diff = await _diffs.CompareWithPreviousAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            if (SelectedSnapshot?.Id != item.Id)
            {
                return;
            }

            Diff = diff;
            DiffSummary = BuildDiffSummary(diff);
        }
        catch (Exception ex)
        {
            _logger.Warn($"计算快照差异失败：{item.Id:N}", ex);
            Diff = null;
            DiffSummary = "无法计算差异";
        }
    }

    private static string BuildDiffSummary(SnapshotDiff diff)
    {
        if (!diff.HasPrevious)
        {
            return "首个快照：没有可对比的上一个版本";
        }

        if (diff.IsIdentical)
        {
            return "与上一快照内容相同（仅触发原因或备注不同）";
        }

        var parts = new List<string>();
        if (diff.AddedCount > 0)
        {
            parts.Add($"+{diff.AddedCount} 新增");
        }

        if (diff.ModifiedCount > 0)
        {
            parts.Add($"~{diff.ModifiedCount} 修改");
        }

        if (diff.RemovedCount > 0)
        {
            parts.Add($"-{diff.RemovedCount} 删除");
        }

        var size = diff.SizeDelta >= 0
            ? $"+{RestorePlan.FormatBytes(diff.SizeDelta)}"
            : $"-{RestorePlan.FormatBytes(-diff.SizeDelta)}";

        return $"{string.Join("   ", parts)}   ·   体积 {size}";
    }

    [RelayCommand]
    private void SelectGame(GameListItemViewModel? item)
    {
        if (item is not null)
        {
            SelectedGame = item;
        }
    }

    [RelayCommand]
    private void BackToGames()
    {
        SelectedGame = null;
        Diff = null;
        DiffSummary = string.Empty;
    }

    [RelayCommand]
    private async Task ScanGamesAsync()
    {
        var added = _dialogs.ShowWizard(startStep: 1);
        if (added > 0)
        {
            await LoadAsync().ConfigureAwait(true);
            StatusMessage = $"已添加 {added} 个游戏";
        }
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var result = _dialogs.ShowAddGame();
        if (result is null)
        {
            return;
        }

        var game = new Game
        {
            Name = result.Name.Trim(),
            Source = GameSource.Manual,
            InstallDir = string.IsNullOrWhiteSpace(result.InstallDir) ? null : result.InstallDir.Trim(),
            CreatedAt = _clock.UtcNowMs,
            UpdatedAt = _clock.UtcNowMs,
        };
        game.ExeNames.AddRange(result.ExeNames);

        var excludes = result.Excludes
            .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var directory in result.Directories)
        {
            var saveRoot = SaveRootResolver.CreateSaveRoot(directory, _tokens);
            saveRoot.Exclude.AddRange(excludes);
            game.SaveRoots.Add(saveRoot);

            if (!saveRoot.IsTokenized)
            {
                _logger.Warn($"目录不在标准位置，只能以绝对路径保存（跨机需要重新定位）：{directory}");
            }
        }

        if (game.SaveRoots.Count == 0)
        {
            _dialogs.ShowError("至少要选择一个存档目录。");
            return;
        }

        await _games.UpsertAsync(game, CancellationToken.None).ConfigureAwait(true);
        _logger.Info($"添加游戏：{game.Name}（{game.SaveRoots.Count} 个存档根）");
        await LoadAsync().ConfigureAwait(true);
        SelectedGame = Games.FirstOrDefault(g => g.Id == game.Id);
    }

    [RelayCommand]
    private async Task RemoveGameAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        var name = SelectedGame.Name;
        await _games.DeleteAsync(SelectedGame.Id, CancellationToken.None).ConfigureAwait(true);
        _logger.Info($"删除游戏关联（不动源文件与云端数据）：{name}");
        await LoadAsync().ConfigureAwait(true);
        StatusMessage = $"已删除 {name}（源文件与云端快照未受影响）";
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (SelectedGame is null || IsBusy)
        {
            return;
        }

        var game = SelectedGame;
        IsBusy = true;
        StatusMessage = $"正在备份 {game.Name}…";

        try
        {
            var note = _dialogs.PromptNote($"为「{game.Name}」创建快照（备注可留空）");
            var result = await _snapshotService
                .CreateAsync(game.Id, SnapshotTrigger.Manual, string.IsNullOrWhiteSpace(note) ? null : note,
                    CancellationToken.None)
                .ConfigureAwait(true);

            var message = $"快照完成：{result.FileCount} 个文件，{RestorePlan.FormatBytes(result.TotalSize)}";
            if (result.Skipped.Count > 0)
            {
                message += $"（跳过 {result.Skipped.Count} 个文件）";
            }

            if (result.SnapshotId is { } snapshotId)
            {
                var upload = await _uploadService.UploadSnapshotAsync(snapshotId, CancellationToken.None)
                    .ConfigureAwait(true);
                message += upload.Success ? "，已上传" : $"，待上传：{upload.Error}";
            }

            StatusMessage = message;
            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == game.Id);
        }
        catch (NeedsRelocateException)
        {
            _dialogs.ShowError("存档目录在本机不存在（可能被移动或删除）。请重新定位或删除该游戏后重新添加。");
            await LoadAsync().ConfigureAwait(true);
        }
        catch (NoFilesFoundException)
        {
            _dialogs.ShowError("这个目录里没有可备份的文件，已放弃创建空快照。");
        }
        catch (Exception ex)
        {
            _logger.Error("备份失败", ex);
            _dialogs.ShowError($"备份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryUploadAsync()
    {
        if (SelectedGame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "正在重试上传…";
        try
        {
            var pending = await _snapshots.ListByGameAsync(SelectedGame.Id, CancellationToken.None).ConfigureAwait(true);
            var targets = pending.Where(s => s.RemoteState == RemoteSnapshotState.Pending).ToList();

            if (targets.Count == 0)
            {
                StatusMessage = "没有待上传的快照";
                return;
            }

            var success = 0;
            string? lastError = null;
            foreach (var snapshot in targets)
            {
                var outcome = await _uploadService.UploadSnapshotAsync(snapshot.Id, CancellationToken.None)
                    .ConfigureAwait(true);
                if (outcome.Success)
                {
                    success++;
                }
                else
                {
                    lastError = outcome.Error;
                }
            }

            StatusMessage = lastError is null
                ? $"已上传 {success} 个快照"
                : $"成功 {success} / {targets.Count}，最后错误：{lastError}";
            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == SelectedGame.Id);
        }
        catch (Exception ex)
        {
            _logger.Error("重试上传失败", ex);
            _dialogs.ShowError($"重试上传失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (SelectedSnapshot is null || IsBusy)
        {
            return;
        }

        var target = SelectedSnapshot.Snapshot;
        IsBusy = true;
        try
        {
            var plan = await _restoreService.PlanAsync(target.Id, CancellationToken.None).ConfigureAwait(true);

            if (!plan.CanExecute)
            {
                _dialogs.ShowError(
                    "目标目录在本机无法解析，需要重新定位：\r\n" + string.Join("\r\n", plan.UnresolvedRoots));
                return;
            }

            if (plan.Operations.Count == 0)
            {
                _dialogs.ShowInfo("当前文件与这个快照完全一致，无需恢复。");
                return;
            }

            if (!_dialogs.ConfirmRestore(plan))
            {
                return;
            }

            StatusMessage = "正在恢复…";
            var progress = new Progress<RestoreProgress>(p =>
                StatusMessage = $"恢复中 {p.Completed}/{p.Total}：{p.CurrentPath}");

            var result = await _restoreService
                .ExecuteAsync(plan, progress, CancellationToken.None)
                .ConfigureAwait(true);

            StatusMessage = result.Success
                ? $"恢复完成：写入 {result.AppliedCount}，删除 {result.DeletedCount}，耗时 {result.ElapsedMs} ms"
                : $"恢复部分失败：成功 {result.AppliedCount + result.DeletedCount}，失败 {result.Failures.Count}";

            if (!result.Success)
            {
                _dialogs.ShowError(
                    "以下文件恢复失败（其余已成功）：\r\n" +
                    string.Join("\r\n", result.Failures.Take(15).Select(f => $"{f.RelPath}：{f.Reason}")) +
                    (result.Failures.Count > 15 ? $"\r\n…（共 {result.Failures.Count} 个）" : string.Empty));
            }

            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == SelectedGame?.Id);
        }
        catch (RestoreRequiresRelocateException ex)
        {
            _dialogs.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("恢复失败", ex);
            _dialogs.ShowError($"恢复失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (SelectedGame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await _retention.PreviewAsync(SelectedGame.Id, CancellationToken.None).ConfigureAwait(true);
            if (plan.IsEmpty)
            {
                _dialogs.ShowInfo("没有符合保留策略的旧快照需要清理。");
                return;
            }

            if (!_dialogs.ConfirmRetention(plan))
            {
                return;
            }

            var result = await _retention.ApplyAsync(plan, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = $"已清理 {result.PrunedSnapshots} 个旧快照，删除 {result.DeletedObjects} 个对象，" +
                            $"释放 {RestorePlan.FormatBytes(result.BytesFreed)}";
            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == SelectedGame?.Id);
        }
        catch (Exception ex)
        {
            _logger.Error("清理旧快照失败", ex);
            _dialogs.ShowError($"清理失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResolveConflictAsync()
    {
        if (SelectedGame is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var view = await _conflicts.GetAsync(SelectedGame.Id, CancellationToken.None).ConfigureAwait(true);
            if (view is null)
            {
                _dialogs.ShowInfo("这个游戏当前没有冲突。");
                return;
            }

            var choice = _dialogs.ShowConflict(view, out var leftId, out var rightId);
            switch (choice)
            {
                case ConflictChoice.Left:
                    await _conflicts.ResolveAsync(SelectedGame.Id, leftId, CancellationToken.None).ConfigureAwait(true);
                    StatusMessage = "已选择 A 侧，冲突标记已清除";
                    break;

                case ConflictChoice.Right:
                    await _conflicts.ResolveAsync(SelectedGame.Id, rightId, CancellationToken.None).ConfigureAwait(true);
                    StatusMessage = "已选择 B 侧，冲突标记已清除";
                    break;

                case ConflictChoice.ExportBoth:
                {
                    var target = _dialogs.PickSaveFile($"conflict-{SelectedGame.Name}.zip");
                    if (target is null)
                    {
                        return;
                    }

                    var folder = Path.GetDirectoryName(target) ?? ".";
                    var leftZip = Path.Combine(folder, $"A-{Path.GetFileName(target)}");
                    var rightZip = Path.Combine(folder, $"B-{Path.GetFileName(target)}");
                    await _exporter.ExportAsync(leftId, leftZip, CancellationToken.None).ConfigureAwait(true);
                    await _exporter.ExportAsync(rightId, rightZip, CancellationToken.None).ConfigureAwait(true);
                    StatusMessage = $"两侧已导出：{leftZip} / {rightZip}";
                    return;
                }

                default:
                    return;
            }

            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == SelectedGame?.Id);
        }
        catch (Exception ex)
        {
            _logger.Error("处理冲突失败", ex);
            _dialogs.ShowError($"处理冲突失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RelocateAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        var game = SelectedGame.Model;
        var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
        var unresolved = resolution.Roots
            .Where(r => r.AbsolutePath is null || !r.DirectoryExists)
            .Select(r => r.Root.Token)
            .ToList();

        if (unresolved.Count == 0)
        {
            _dialogs.ShowInfo("该游戏的所有存档目录在本机都能找到，不需要重新定位。");
            return;
        }

        var mapping = _dialogs.ShowRelocate(game.Name, unresolved);
        if (mapping is null || mapping.Count == 0)
        {
            return;
        }

        foreach (var (token, path) in mapping)
        {
            game.PathOverrides[token] = path;
        }

        var after = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
        game.NeedsRelocate = !after.IsComplete;
        await _games.UpsertAsync(game, CancellationToken.None).ConfigureAwait(true);

        _logger.Info($"重新定位：{game.Name} 映射了 {mapping.Count} 个存档根");
        await LoadAsync().ConfigureAwait(true);
        SelectedGame = Games.FirstOrDefault(g => g.Id == game.Id);
        StatusMessage = after.IsComplete ? "重新定位完成" : "仍有目录未找到，可再次定位";
    }

    /// <summary>
    /// 一键刷新存档路径：重新抓取外部存档路径站点的数据并覆盖已有的存档根。
    /// 失败时保留原有数据，只提示错误——外部站点挂掉绝不能让用户的配置被清空。
    /// </summary>
    [RelayCommand]
    private async Task RefreshSavePathsAsync()
    {
        if (SelectedGame is null || IsRefreshingSavePaths)
        {
            return;
        }

        var game = SelectedGame.Model;
        IsRefreshingSavePaths = true;
        RefreshSavePathsStatus = "正在刷新存档路径…";

        try
        {
            var result = await _refreshPaths.RefreshAsync(game.Name, game.InstallDir, CancellationToken.None)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                // 失败：原样保留已有存档根，只把原因告诉用户
                RefreshSavePathsStatus = result.Message;
                StatusMessage = result.Message;
                _logger.Warn($"刷新存档路径失败（保留原数据）：{game.Name} / {result.Message}");
                return;
            }

            // 成功：覆盖存档根。保留原根上的排除/包含规则，避免用户白填一遍。
            var oldRoots = game.SaveRoots.ToDictionary(
                r => r.Token,
                r => r,
                StringComparer.OrdinalIgnoreCase);

            var merged = new List<SaveRoot>();
            foreach (var root in result.Roots)
            {
                if (oldRoots.TryGetValue(root.Token, out var existing))
                {
                    root.Include.AddRange(existing.Include);
                    root.Exclude.AddRange(existing.Exclude);
                }

                merged.Add(root);
            }

            game.SaveRoots = merged;
            game.UpdatedAt = _clock.UtcNowMs;

            var resolution = SaveRootResolver.Resolve(game, _tokens, _fileSystem);
            game.NeedsRelocate = !resolution.IsComplete;

            await _games.UpsertAsync(game, CancellationToken.None).ConfigureAwait(true);
            _logger.Info($"刷新存档路径：{game.Name} → {merged.Count} 个存档根");

            RefreshSavePathsStatus = result.Message;
            StatusMessage = result.Message;

            await LoadAsync().ConfigureAwait(true);
            SelectedGame = Games.FirstOrDefault(g => g.Id == game.Id);
        }
        catch (Exception ex)
        {
            _logger.Error("刷新存档路径失败", ex);
            RefreshSavePathsStatus = $"刷新失败：{ex.Message}";
            StatusMessage = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsRefreshingSavePaths = false;
        }
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (SelectedGame is null || IsBusy)
        {
            return;
        }

        var game = SelectedGame.Model;
        var dirs = _dialogs.ShowProbe(game.Name, game.InstallDir);
        if (dirs is null || dirs.Count == 0)
        {
            return;
        }

        var existing = game.SaveRoots.Select(r => r.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var dir in dirs)
        {
            var root = SaveRootResolver.CreateSaveRoot(dir, _tokens);
            if (!existing.Add(root.Token))
            {
                continue;
            }

            game.SaveRoots.Add(root);
            added++;
        }

        if (added == 0)
        {
            StatusMessage = "所选目录已经在该游戏里了";
            return;
        }

        game.NeedsRelocate = !SaveRootResolver.Resolve(game, _tokens, _fileSystem).IsComplete;
        await _games.UpsertAsync(game, CancellationToken.None).ConfigureAwait(true);
        _logger.Info($"探测模式为 {game.Name} 添加了 {added} 个存档根");
        await LoadAsync().ConfigureAwait(true);
        SelectedGame = Games.FirstOrDefault(g => g.Id == game.Id);
        StatusMessage = $"已添加 {added} 个存档目录，下次备份会包含它们";
    }

    [RelayCommand]
    private async Task ExportSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        var name = $"{SelectedGame?.Name ?? "snapshot"}-{TimeUtil.FormatLocal(SelectedSnapshot.Snapshot.CreatedAt).Replace(':', '-').Replace(' ', '_')}.zip";
        var target = _dialogs.PickSaveFile(name);
        if (target is null)
        {
            return;
        }

        try
        {
            await _exporter.ExportAsync(SelectedSnapshot.Id, target, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = $"已导出到 {target}";
        }
        catch (Exception ex)
        {
            _logger.Error("导出快照失败", ex);
            _dialogs.ShowError($"导出失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenSaveFolder()
    {
        if (SelectedGame is null)
        {
            return;
        }

        var resolution = SaveRootResolver.Resolve(SelectedGame.Model, _tokens, _fileSystem);
        var path = resolution.Roots.FirstOrDefault(r => r.DirectoryExists)?.AbsolutePath;
        if (path is null)
        {
            _dialogs.ShowError("该游戏的存档目录在本机不存在，需要重新定位。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"打开目录失败：{ex.Message}");
        }
    }

    private void QueueCoverLoad(GameListItemViewModel item)
    {
        // 所有适用形态都已经在缓存里了，不用再下载
        var applicable = PosterKinds.All.Where(k => CoverService.SupportsKind(item.Model, k)).ToList();
        if (applicable.Count == 0)
        {
            return;
        }

        if (applicable.All(k => item.AvailablePosters.ContainsKey(k)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var posters = await LoadPostersAsync(item.Model).ConfigureAwait(false);
            if (posters.Count > 0)
            {
                item.ApplyPosters(posters, ResolvePreferredPoster(item.Model));
            }
        });
    }

    /// <summary>下载该游戏所有形态的海报，返回"形态 → 本地路径"（下不到的形态不出现）。</summary>
    private async Task<IReadOnlyDictionary<PosterKind, string>> LoadPostersAsync(Game model)
    {
        var loaded = await _covers.GetAllAsync(model, CancellationToken.None).ConfigureAwait(false);
        var result = new Dictionary<PosterKind, string>();
        foreach (var (kind, path) in loaded)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                result[kind] = path;
            }
        }

        return result;
    }

    /// <summary>用户偏好的海报形态（没设过就用竖屏长图）。</summary>
    private static PosterKind ResolvePreferredPoster(Game model)
    {
        return PosterKinds.TryParse(model.PosterKind, out var kind) ? kind : PosterKind.Portrait;
    }

    /// <summary>
    /// 把用户选中的海报形态记到游戏上（下次打开还是这个）。
    /// 由 <see cref="GameListItemViewModel.PosterKindChanged"/> 事件驱动。
    /// </summary>
    private void SavePosterPreference(GameListItemViewModel item)
    {
        var slug = item.SelectedPosterKind.Slug();
        if (string.Equals(item.Model.PosterKind, slug, StringComparison.Ordinal))
        {
            return;
        }

        item.Model.PosterKind = slug;
        item.Model.UpdatedAt = _clock.UtcNowMs;
        _ = _games.UpsertAsync(item.Model, CancellationToken.None);
    }
}
