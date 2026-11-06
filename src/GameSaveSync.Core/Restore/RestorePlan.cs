namespace GameSaveSync.Core.Restore;

public enum RestoreOperationKind
{
    Add = 0,
    Overwrite = 1,
    Delete = 2,
}

public sealed record RestoreOperation(
    RestoreOperationKind Kind,
    int RootIndex,
    string RelPath,
    string AbsolutePath,
    long Size,
    string? Hash,
    string? ExistingHash);

/// <summary>
/// 恢复计划（两阶段的第一阶段）。先给用户看"新增/覆盖/删除哪些文件 + 总字节数"，
/// 确认后才执行。<see cref="CanExecute"/> 为 false 时必须走"重新定位"，绝不猜测路径。
/// </summary>
public sealed class RestorePlan
{
    public Guid SnapshotId { get; init; }

    public Guid GameId { get; init; }

    public string GameName { get; init; } = string.Empty;

    public Guid? SnapshotParentId { get; init; }

    public long SnapshotCreatedAt { get; init; }

    public IReadOnlyList<RestoreOperation> Operations { get; init; } = Array.Empty<RestoreOperation>();

    /// <summary>本机无法解析的令牌 / 不存在的目录 → 必须重新定位。</summary>
    public IReadOnlyList<string> UnresolvedRoots { get; init; } = Array.Empty<string>();

    public int UnchangedCount { get; init; }

    public bool CanExecute => UnresolvedRoots.Count == 0;

    public int AddCount => Operations.Count(o => o.Kind == RestoreOperationKind.Add);

    public int OverwriteCount => Operations.Count(o => o.Kind == RestoreOperationKind.Overwrite);

    public int DeleteCount => Operations.Count(o => o.Kind == RestoreOperationKind.Delete);

    public long AddBytes => Operations.Where(o => o.Kind == RestoreOperationKind.Add).Sum(o => o.Size);

    public long OverwriteBytes => Operations.Where(o => o.Kind == RestoreOperationKind.Overwrite).Sum(o => o.Size);

    public long DeleteBytes => Operations.Where(o => o.Kind == RestoreOperationKind.Delete).Sum(o => o.Size);

    public int TotalOperations => Operations.Count;

    /// <summary>计划摘要（供确认弹窗显示）。</summary>
    public string Describe() =>
        $"新增 {AddCount} 个（{FormatBytes(AddBytes)}），覆盖 {OverwriteCount} 个（{FormatBytes(OverwriteBytes)}），" +
        $"删除 {DeleteCount} 个（{FormatBytes(DeleteBytes)}），无变化 {UnchangedCount} 个";

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}

public sealed record RestoreProgress(int Completed, int Total, string CurrentPath, RestoreOperationKind Kind);

public sealed record RestoreFailure(RestoreOperationKind Kind, string RelPath, string Reason);

public sealed record RestoreResult(
    Guid SnapshotId,
    Guid? PreRestoreSnapshotId,
    int AppliedCount,
    int DeletedCount,
    IReadOnlyList<RestoreFailure> Failures,
    long ElapsedMs)
{
    public bool Success => Failures.Count == 0;
}

/// <summary>目标路径需要重新定位（令牌缺失或目录被移动），拒绝执行。</summary>
public sealed class RestoreRequiresRelocateException : Exception
{
    public RestoreRequiresRelocateException(IReadOnlyList<string> unresolvedRoots)
        : base($"目标路径无法在本机解析，需重新定位：{string.Join("、", unresolvedRoots)}") =>
        UnresolvedRoots = unresolvedRoots;

    public IReadOnlyList<string> UnresolvedRoots { get; }
}
