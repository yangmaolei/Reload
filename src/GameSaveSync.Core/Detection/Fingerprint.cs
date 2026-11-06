namespace GameSaveSync.Core.Detection;

/// <summary>
/// 文件身份：卷序列号 + 文件 ID（对应 Win32 的 FILE_ID_INFO）。
/// 用于识别"改名/移动"以及给 L2 元数据过滤提供比 mtime 更强的依据。
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileId)
{
    public static FileIdentity None => new(0, 0);

    public bool IsKnown => FileId != 0;
}

/// <summary>读取文件身份（实现：Infrastructure.Win32FileIdentityProvider）。</summary>
public interface IFileIdentityProvider
{
    FileIdentity TryGet(string path);
}

/// <summary>某个文件在"上一次快照"时的元数据快照（L2 层）。</summary>
public sealed record FileFingerprint(
    long Size,
    long MtimeMs,
    ulong VolumeSerial,
    ulong FileId,
    string Hash)
{
    public bool Matches(FileEntryLike current) =>
        Size == current.Size && MtimeMs == current.MtimeMs;
}

/// <summary>避免 Core.Detection 依赖 Abstractions.FileEntry 的循环，这里只留最小字段。</summary>
public readonly record struct FileEntryLike(long Size, long MtimeMs, ulong VolumeSerial, ulong FileId);

/// <summary>
/// L2 指纹缓存：把"上次快照时每个文件的 size/mtime/文件 ID/哈希"存库。
/// 用途有两个：① 未变文件直接复用哈希，不重复读盘；② 应用退出期间的变化在启动时能被发现。
/// </summary>
public interface IFingerprintStore
{
    /// <summary>键为 <c>{rootIndex}|{relPathKey}</c>。</summary>
    Task<IReadOnlyDictionary<string, FileFingerprint>> LoadAsync(Guid gameId, CancellationToken ct);

    Task SaveAsync(Guid gameId, IReadOnlyList<FingerprintRecord> items, CancellationToken ct);

    Task ClearAsync(Guid gameId, CancellationToken ct);
}

public sealed record FingerprintRecord(
    int RootIndex,
    string RelPathKey,
    long Size,
    long MtimeMs,
    ulong VolumeSerial,
    ulong FileId,
    string Hash);

public static class FingerprintKeys
{
    public static string Build(int rootIndex, string relPathKey) => $"{rootIndex}|{relPathKey}";
}
