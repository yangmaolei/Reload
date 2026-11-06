using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Snapshots;

/// <summary>清单的规范化序列化与哈希。清单哈希用于校验"清单本身"没被改过。</summary>
public static class SnapshotManifestSerializer
{
    public static string ToJson(SnapshotManifest manifest) =>
        System.Text.Json.JsonSerializer.Serialize(manifest, Storage.JsonColumns.Options);

    public static SnapshotManifest FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<SnapshotManifest>(json, Storage.JsonColumns.Options)
        ?? throw new InvalidDataException("快照清单为空或格式非法");

    public static string ComputeHash(SnapshotManifest manifest, IHashService hashService)
    {
        var json = ToJson(manifest);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return hashService.HashBytes(bytes);
    }
}

/// <summary>存档根目录的令牌在本机无法解析 → 进入"重新定位"流程，绝不生成空快照。</summary>
public sealed class NeedsRelocateException : Exception
{
    public NeedsRelocateException(Guid gameId, IReadOnlyList<string> missingTokens)
        : base($"游戏 {gameId} 的存档路径无法在本机解析：{string.Join("、", missingTokens)}")
    {
        GameId = gameId;
        MissingTokens = missingTokens;
    }

    public Guid GameId { get; }

    public IReadOnlyList<string> MissingTokens { get; }
}

/// <summary>没有扫描到任何文件（目录被删除/清空）→ 不生成空快照。</summary>
public sealed class NoFilesFoundException : Exception
{
    public NoFilesFoundException(Guid gameId)
        : base($"游戏 {gameId} 的存档目录中没有可备份的文件，已放弃创建快照") => GameId = gameId;

    public Guid GameId { get; }
}

public sealed record SkippedFileInfo(int RootIndex, string RelPath, string Reason);

/// <summary>快照结果。<see cref="Created"/> 为 false 时 <see cref="SkipReason"/> 说明原因。</summary>
public sealed record SnapshotResult(
    Guid GameId,
    Guid? SnapshotId,
    bool Created,
    string? SkipReason,
    int FileCount,
    long TotalSize,
    int NewObjects,
    int ReusedObjects,
    IReadOnlyList<SkippedFileInfo> Skipped);
