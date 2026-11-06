using CommunityToolkit.Mvvm.ComponentModel;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.App.Views;
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

    [ObservableProperty]
    private string metaText = string.Empty;

    public bool HasCover => !string.IsNullOrWhiteSpace(CoverPath);

    partial void OnCoverPathChanged(string? value) => OnPropertyChanged(nameof(HasCover));

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
