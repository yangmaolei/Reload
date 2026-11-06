using System.Text.Json.Serialization;

namespace GameSaveSync.Core.Models;

/// <summary>
/// 快照清单：全量状态（不是增量）。会上传到
/// <c>games/&lt;gameId&gt;/snapshots/&lt;ts&gt;-&lt;sid&gt;.json</c>，也是恢复的唯一依据。
/// 清单里带 <see cref="Roots"/>（令牌化路径），所以跨机器恢复时可以重新解析本机路径。
/// </summary>
public sealed class SnapshotManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("snapshotId")]
    public Guid SnapshotId { get; set; }

    [JsonPropertyName("gameId")]
    public Guid GameId { get; set; }

    [JsonPropertyName("gameName")]
    public string GameName { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = SnapshotTriggerNames.Manual;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>本快照涉及的存档根（令牌化路径），下标与 <see cref="ManifestFile.Root"/> 对应。</summary>
    [JsonPropertyName("roots")]
    public List<ManifestRoot> Roots { get; set; } = new();

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }

    [JsonPropertyName("files")]
    public List<ManifestFile> Files { get; set; } = new();

    /// <summary>被跳过的文件（占用/权限/不稳定），仅用于审计，不参与恢复。</summary>
    [JsonPropertyName("skipped")]
    public List<ManifestSkipped> Skipped { get; set; } = new();
}

public sealed class ManifestRoot
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("isTokenized")]
    public bool IsTokenized { get; set; } = true;
}

public sealed class ManifestFile
{
    [JsonPropertyName("root")]
    public int Root { get; set; }

    [JsonPropertyName("relPath")]
    public string RelPath { get; set; } = string.Empty;

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    [JsonPropertyName("attrs")]
    public int Attrs { get; set; }
}

public sealed class ManifestSkipped
{
    [JsonPropertyName("root")]
    public int Root { get; set; }

    [JsonPropertyName("relPath")]
    public string RelPath { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
