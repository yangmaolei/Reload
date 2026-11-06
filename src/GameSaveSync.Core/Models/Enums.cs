namespace GameSaveSync.Core.Models;

/// <summary>游戏来源。M1 只用 Manual，其余枚举为 M4 预留。</summary>
public enum GameSource
{
    Manual = 0,
    Steam = 1,
    Epic = 2,
    Gog = 3,
    Ubisoft = 4,
    Uwp = 5,

    /// <summary>Xbox / 微软商店的 MSIX(GDK) 游戏：Xbox 应用、Game Pass、主机游戏在 PC 上运行。</summary>
    Xbox = 6,
}

/// <summary>快照触发原因（M1 只产生 Manual / PreRestore，其余枚举一次建好，M2 不再改表）。</summary>
public enum SnapshotTrigger
{
    PreLaunch = 0,
    Exit = 1,
    Idle = 2,
    PostChange = 3,
    PreRestore = 4,
    Manual = 5,
}

public static class SnapshotTriggerNames
{
    public const string PreLaunch = "pre_launch";
    public const string Exit = "exit";
    public const string Idle = "idle";
    public const string PostChange = "post_change";
    public const string PreRestore = "pre_restore";
    public const string Manual = "manual";

    public static string ToDbValue(this SnapshotTrigger trigger) => trigger switch
    {
        SnapshotTrigger.PreLaunch => PreLaunch,
        SnapshotTrigger.Exit => Exit,
        SnapshotTrigger.Idle => Idle,
        SnapshotTrigger.PostChange => PostChange,
        SnapshotTrigger.PreRestore => PreRestore,
        SnapshotTrigger.Manual => Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "未知的快照触发类型"),
    };

    public static SnapshotTrigger Parse(string value) => value switch
    {
        PreLaunch => SnapshotTrigger.PreLaunch,
        Exit => SnapshotTrigger.Exit,
        Idle => SnapshotTrigger.Idle,
        PostChange => SnapshotTrigger.PostChange,
        PreRestore => SnapshotTrigger.PreRestore,
        Manual => SnapshotTrigger.Manual,
        _ => throw new ArgumentException($"未知的快照触发类型：{value}", nameof(value)),
    };

    public static string ToDisplayName(this SnapshotTrigger trigger) => trigger switch
    {
        SnapshotTrigger.PreLaunch => "启动前",
        SnapshotTrigger.Exit => "游戏退出",
        SnapshotTrigger.Idle => "空闲",
        SnapshotTrigger.PostChange => "文件变更",
        SnapshotTrigger.PreRestore => "恢复前保险点",
        SnapshotTrigger.Manual => "手动",
        _ => trigger.ToString(),
    };
}

public enum LocalSnapshotState
{
    Present = 0,
    Pruned = 1,
}

public enum RemoteSnapshotState
{
    Pending = 0,
    Uploaded = 1,
    Missing = 2,
    Conflict = 3,
}

public static class SnapshotStates
{
    public static string ToDbValue(this LocalSnapshotState state) => state switch
    {
        LocalSnapshotState.Present => "present",
        LocalSnapshotState.Pruned => "pruned",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static LocalSnapshotState ParseLocal(string value) => value switch
    {
        "present" => LocalSnapshotState.Present,
        "pruned" => LocalSnapshotState.Pruned,
        _ => throw new ArgumentException($"未知的本地快照状态：{value}", nameof(value)),
    };

    public static string ToDbValue(this RemoteSnapshotState state) => state switch
    {
        RemoteSnapshotState.Pending => "pending",
        RemoteSnapshotState.Uploaded => "uploaded",
        RemoteSnapshotState.Missing => "missing",
        RemoteSnapshotState.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static RemoteSnapshotState ParseRemote(string value) => value switch
    {
        "pending" => RemoteSnapshotState.Pending,
        "uploaded" => RemoteSnapshotState.Uploaded,
        "missing" => RemoteSnapshotState.Missing,
        "conflict" => RemoteSnapshotState.Conflict,
        _ => throw new ArgumentException($"未知的远端快照状态：{value}", nameof(value)),
    };
}
