using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.App.Views;
using GameSaveSync.Core.Covers;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Retention;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.App.ViewModels;

public enum GameSyncState
{
    Neutral = 0,
    Synced = 1,
    Pending = 2,
    Conflict = 3,
    NeedsRelocate = 4,
    Failed = 5,
}

public sealed record AddGameResult(
    string Name,
    IReadOnlyList<string> Directories,
    string Excludes,
    string? InstallDir,
    IReadOnlyList<string> ExeNames);

/// <summary>UI 侧的唯一"弹窗"入口，便于 ViewModel 单测。</summary>
public interface IDialogService
{
    AddGameResult? ShowAddGame();

    /// <summary>恢复前把计划摊给用户确认（新增/覆盖/删除 + 字节数）。</summary>
    bool ConfirmRestore(RestorePlan plan);

    /// <summary>清理旧快照前把计划摊给用户（数量、可释放空间、待删对象）。</summary>
    bool ConfirmRetention(RetentionPlan plan);

    /// <summary>并排展示冲突两侧，返回用户的选择（含"导出两侧 zip"）。</summary>
    ConflictChoice ShowConflict(ConflictView view, out Guid leftId, out Guid rightId);

    /// <summary>重新定位：让用户为每个解析不到的存档根指定本机目录。</summary>
    IReadOnlyDictionary<string, string>? ShowRelocate(string gameName, IReadOnlyList<string> unresolvedRoots);

    string? PickSaveFile(string suggestedName);

    /// <summary>选一个目录（批量导出存档的落盘位置）。取消返回 null。</summary>
    string? PickFolder(string suggestedName, string description);

    /// <summary>
    /// 展示批量操作的结果明细（成功/失败逐条列出）。
    /// </summary>
    void ShowBatchResult(string title, IReadOnlyList<string> lines);

    string? PromptNote(string title, string initial = "");

    void ShowInfo(string message, string title = "Reload");

    void ShowError(string message, string title = "出错了");

    /// <summary>首次向导 / 扫描游戏：返回新添加的游戏数（0 表示取消）。</summary>
    int ShowWizard(int startStep);

    /// <summary>探测模式：返回用户勾选的候选目录（取消返回 null）。</summary>
    IReadOnlyList<string>? ShowProbe(string gameName, string? installDir);
}

public sealed partial class GameListItemViewModel : ObservableObject
{
    public GameListItemViewModel(Game model) => Model = model;

    public Game Model { get; }

    public Guid Id => Model.Id;

    public string Name => Model.Name;

    public string PlatformText => Model.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic",
        GameSource.Gog => "GOG",
        GameSource.Ubisoft => "Ubisoft",
        GameSource.Uwp => "UWP",
        GameSource.Xbox => "Xbox",
        _ => "手动",
    };

    public string Detail => Model.NeedsRelocate
        ? "路径需重新定位"
        : string.Join("  ·  ", Model.SaveRoots.Select(r => r.Token));

    /// <summary>卡片上只展示前两个存档根，避免长路径把布局撑开。</summary>
    public string RootSummary
    {
        get
        {
            if (Model.NeedsRelocate)
            {
                return "存档目录需重新定位";
            }

            var roots = Model.SaveRoots.Select(r => Shorten(r.Token)).Take(2).ToList();
            var suffix = Model.SaveRoots.Count > roots.Count ? $" 等 {Model.SaveRoots.Count} 处" : string.Empty;
            return string.Join("  ·  ", roots) + suffix;
        }
    }

    [ObservableProperty]
    private GameSyncState state = GameSyncState.Neutral;

    [ObservableProperty]
    private string stateText = "未备份";

    [ObservableProperty]
    private string lastBackupText = "从未备份";

    [ObservableProperty]
    private int snapshotCount;

    [ObservableProperty]
    private string? coverPath;

    /// <summary>
    /// 当前形态可用的海报（形态 → 本地路径）。为空表示没有可用海报，UI 退回首字母色块。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<PosterKind, string> availablePosters =
        new Dictionary<PosterKind, string>();

    /// <summary>用户选中的海报形态。</summary>
    [ObservableProperty]
    private PosterKind selectedPosterKind = PosterKind.Portrait;

    [ObservableProperty]
    private string metaText = string.Empty;

    /// <summary>
    /// 是否被勾选（批量操作的选择状态）。
    /// <para>
    /// 与"单选"（<see cref="MainViewModel.SelectedGame"/>，决定右侧详情页显示谁）
    /// 是两套独立状态：勾选只影响批量备份/导出，点击卡片进详情不受影响。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool isChecked;

    /// <summary>勾选状态变化时触发，供列表页更新"已选 N 个"计数。</summary>
    public event Action<GameListItemViewModel>? CheckedChanged;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this);

    public bool HasCover => !string.IsNullOrWhiteSpace(CoverPath);

    /// <summary>可切换的海报数量（1 表示没有可切换的第二张）。</summary>
    public int PosterCount => AvailablePosters.Count;

    /// <summary>是否需要显示切换控件（至少两种形态可用）。</summary>
    public bool HasMultiplePosters => AvailablePosters.Count > 1;

    /// <summary>分页指示器文案，形如 "1/2"。</summary>
    public string PosterIndicator => $"{PosterIndex + 1}/{PosterCount}";

    /// <summary>当前海报在可用列表中的序号（从 0 开始）。</summary>
    public int PosterIndex
    {
        get
        {
            var kinds = AvailablePosters.Keys.ToList();
            var index = kinds.IndexOf(SelectedPosterKind);
            return index < 0 ? 0 : index;
        }
    }

    partial void OnCoverPathChanged(string? value) => OnPropertyChanged(nameof(HasCover));

    partial void OnAvailablePostersChanged(IReadOnlyDictionary<PosterKind, string> value)
    {
        OnPropertyChanged(nameof(PosterCount));
        OnPropertyChanged(nameof(HasMultiplePosters));
        OnPropertyChanged(nameof(PosterIndex));
        OnPropertyChanged(nameof(PosterIndicator));

        // 海报集合变了，当前形态可能已经不在里面 —— 重新对齐一次
        SyncCoverPath();
    }

    partial void OnSelectedPosterKindChanged(PosterKind value)
    {
        SyncCoverPath();
        OnPropertyChanged(nameof(PosterIndex));
        OnPropertyChanged(nameof(PosterIndicator));
        PosterKindChanged?.Invoke(this);
    }

    /// <summary>
    /// 按当前选中的形态同步显示的图片。
    /// 单独抽出来是因为"形态值没变"时 CommunityToolkit 不会触发属性变更，
    /// 而海报集合刚填好时恰恰需要刷新一次显示。
    /// </summary>
    private void SyncCoverPath()
    {
        var path = AvailablePosters.TryGetValue(SelectedPosterKind, out var p) ? p : null;
        if (!string.Equals(CoverPath, path, StringComparison.Ordinal))
        {
            CoverPath = path;
        }
    }

    /// <summary>
    /// 应用一批已下载好的海报：按当前偏好选一张显示，并保留其余形态供切换。
    /// </summary>
    public void ApplyPosters(IReadOnlyDictionary<PosterKind, string> posters, PosterKind preferred)
    {
        AvailablePosters = posters;

        if (!posters.ContainsKey(preferred))
        {
            // 偏好的形态没下到，退到列表里第一个可用的
            preferred = posters.Keys.FirstOrDefault();
        }

        SelectedPosterKind = preferred;
    }

    /// <summary>切到下一张海报（循环）。没有第二张时返回 false。</summary>
    public bool NextPoster()
    {
        if (!HasMultiplePosters)
        {
            return false;
        }

        var kinds = AvailablePosters.Keys.ToList();
        var next = (PosterIndex + 1) % kinds.Count;
        SelectedPosterKind = kinds[next];
        return true;
    }

    /// <summary>切到上一张海报（循环）。没有第二张时返回 false。</summary>
    public bool PreviousPoster()
    {
        if (!HasMultiplePosters)
        {
            return false;
        }

        var kinds = AvailablePosters.Keys.ToList();
        var previous = (PosterIndex - 1 + kinds.Count) % kinds.Count;
        SelectedPosterKind = kinds[previous];
        return true;
    }

    /// <summary>切到下一张海报。</summary>
    [RelayCommand]
    private void SwitchNextPoster() => NextPoster();

    /// <summary>切到上一张海报。</summary>
    [RelayCommand]
    private void SwitchPreviousPoster() => PreviousPoster();

    /// <summary>
    /// 海报形态变化时触发，供外部（如 MainViewModel）持久化用户偏好。
    /// </summary>
    public event Action<GameListItemViewModel>? PosterKindChanged;

    public void Update(Game model, GameSyncState state, string stateText, string lastBackupText, int snapshotCount)
    {
        Model.Name = model.Name;
        Model.SaveRoots = model.SaveRoots;
        Model.NeedsRelocate = model.NeedsRelocate;
        Model.Retention = model.Retention;
        State = state;
        StateText = stateText;
        LastBackupText = lastBackupText;
        SnapshotCount = snapshotCount;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(RootSummary));
        OnPropertyChanged(nameof(PlatformText));
    }

    public async Task RefreshAsync(
        ISnapshotRepository snapshots,
        IIndexStateRepository indexState,
        CancellationToken ct)
    {
        var latest = await snapshots.GetLatestAsync(Id, ct).ConfigureAwait(false);
        var count = await snapshots.CountAsync(Id, ct).ConfigureAwait(false);
        var remoteEntry = await indexState.GetRemoteAsync(Id, ct).ConfigureAwait(false);
        var conflicted = remoteEntry?.Conflict == true;

        SnapshotCount = count;
        LastBackupText = latest is null
            ? "从未备份"
            : $"{TimeUtil.FormatLocal(latest.CreatedAt)}（{latest.Trigger.ToDisplayName()}）";

        (State, StateText) = Model.NeedsRelocate
            ? (GameSyncState.NeedsRelocate, "需重新定位")
            : conflicted
                ? (GameSyncState.Conflict, "冲突")
                : latest is null
                    ? (GameSyncState.Neutral, "未备份")
                    : latest.RemoteState switch
                {
                    RemoteSnapshotState.Uploaded => (GameSyncState.Synced, "已同步"),
                    RemoteSnapshotState.Conflict => (GameSyncState.Conflict, "冲突"),
                    RemoteSnapshotState.Missing => (GameSyncState.Failed, "远端缺失"),
                    _ => (GameSyncState.Pending, "待上传"),
                };

        MetaText = $"{count} 个快照";
    }

    private static string Shorten(string token)
    {
        if (token.Length <= 34)
        {
            return token;
        }

        var parts = token.Split('\\');
        return parts.Length <= 2 ? token[..34] + "…" : $"{parts[0]}\\…\\{parts[^1]}";
    }
}

public sealed class SnapshotListItemViewModel
{
    public SnapshotListItemViewModel(Snapshot snapshot) => Snapshot = snapshot;

    public Snapshot Snapshot { get; }

    public Guid Id => Snapshot.Id;

    public string CreatedAtText => TimeUtil.FormatLocal(Snapshot.CreatedAt);

    public string DateText => Snapshot.CreatedAt > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(Snapshot.CreatedAt).ToLocalTime().ToString("MM-dd")
        : "--";

    public string TimeText => Snapshot.CreatedAt > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(Snapshot.CreatedAt).ToLocalTime().ToString("HH:mm")
        : "--:--";

    public string TriggerText => Snapshot.Trigger.ToDisplayName();

    public string SizeText => RestorePlan.FormatBytes(Snapshot.TotalSize);

    public string FileCountText => Snapshot.SkippedCount > 0
        ? $"{Snapshot.FileCount} 文件（跳过 {Snapshot.SkippedCount}）"
        : $"{Snapshot.FileCount} 文件";

    public string StateText => Snapshot.RemoteState switch
    {
        RemoteSnapshotState.Uploaded => "已上传",
        RemoteSnapshotState.Conflict => "冲突",
        RemoteSnapshotState.Missing => "远端缺失",
        _ => "待上传",
    };

    public bool IsPending => Snapshot.RemoteState != RemoteSnapshotState.Uploaded;

    public string Note => Snapshot.Note ?? string.Empty;

    public bool HasNote => !string.IsNullOrWhiteSpace(Snapshot.Note);

    public string? LastError => Snapshot.LastError;
}
