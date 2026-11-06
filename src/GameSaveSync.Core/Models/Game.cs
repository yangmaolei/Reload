namespace GameSaveSync.Core.Models;

/// <summary>一个被管理的游戏。字段与 SQLite games 表一一对应。</summary>
public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public GameSource Source { get; set; } = GameSource.Manual;

    /// <summary>SteamAppId / Epic CatalogItemId 等，M4 使用。</summary>
    public string? ExternalId { get; set; }

    public string? InstallDir { get; set; }

    /// <summary>用于进程匹配（M2 使用）。</summary>
    public List<string> ExeNames { get; set; } = new();

    public List<SaveRoot> SaveRoots { get; set; } = new();

    public string? CoverUrl { get; set; }

    /// <summary>
    /// 用户选中的海报形态（竖屏长图 / 方形头图）。
    /// 为空表示用默认形态（竖屏长图）。这个偏好只跟本机 UI 走，不参与同步。
    /// </summary>
    public string? PosterKind { get; set; }

    public bool Enabled { get; set; } = true;

    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>任一保存路径的令牌在本机无法展开时为 true（跨机后未重新定位）。</summary>
    public bool NeedsRelocate { get; set; }

    /// <summary>
    /// M3 的"路径覆盖"：键是存档根的令牌化路径，值是本机的绝对目录。
    /// 跨机器后原 token 解析不到时，用它把该游戏映射到本机实际位置（绝不猜路径）。
    /// </summary>
    public Dictionary<string, string> PathOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>快照记录（DB 行）。文件明细在 snapshot_files。</summary>
public sealed class Snapshot
{
    public Guid Id { get; set; }

    public Guid GameId { get; set; }

    /// <summary>时间线父节点：创建本快照前该游戏的当前快照。</summary>
    public Guid? ParentId { get; set; }

    public long CreatedAt { get; set; }

    public SnapshotTrigger Trigger { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    /// <summary>清单自身哈希（规范化 JSON 的 BLAKE3），用于校验清单完整性。</summary>
    public string ManifestHash { get; set; } = string.Empty;

    public long TotalSize { get; set; }

    public int FileCount { get; set; }

    public string? Note { get; set; }

    public LocalSnapshotState LocalState { get; set; } = LocalSnapshotState.Present;

    public RemoteSnapshotState RemoteState { get; set; } = RemoteSnapshotState.Pending;

    public string? ConflictGroup { get; set; }

    /// <summary>最近一次上传失败原因（M1 供 UI 显示）。</summary>
    public string? LastError { get; set; }

    /// <summary>被跳过的文件数（占用/权限/不稳定），UI 显示"N 个文件被跳过"。</summary>
    public int SkippedCount { get; set; }
}

/// <summary>快照中的单个文件条目。</summary>
public sealed class SnapshotFile
{
    public Guid SnapshotId { get; set; }

    /// <summary>相对 save root 的路径，保留原始大小写。</summary>
    public string RelPath { get; set; } = string.Empty;

    /// <summary>Windows 语义比较键（lower-case）。</summary>
    public string RelPathKey { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public long Size { get; set; }

    public long Mtime { get; set; }

    public int Attrs { get; set; }

    /// <summary>所属 save root 在清单 roots 数组中的下标。</summary>
    public int RootIndex { get; set; }
}
