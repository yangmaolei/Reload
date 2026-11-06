using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameSaveSync.Core.Remote;

/// <summary>
/// 远端全局索引 <c>index.json</c>。这是整个协议里**唯一需要并发控制**的文件，
/// 用 <c>If-Match</c> 做乐观锁，412 时按游戏逐个合并。
/// </summary>
public sealed class IndexDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("games")]
    public Dictionary<string, IndexGameEntry> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IndexDocument Clone() => new()
    {
        Version = Version,
        UpdatedAt = UpdatedAt,
        Games = Games.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Clone(),
            StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class IndexGameEntry
{
    /// <summary>每设备单调递增计数器（持久化在本地 devices 表）。</summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("snapshotId")]
    public Guid SnapshotId { get; set; }

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    /// <summary>true 表示两台设备各改过、且快照之间无父子关系，需要用户选择。</summary>
    [JsonPropertyName("conflict")]
    public bool Conflict { get; set; }

    public IndexGameEntry Clone() => new()
    {
        Seq = Seq,
        SnapshotId = SnapshotId,
        DeviceId = DeviceId,
        UpdatedAt = UpdatedAt,
        Conflict = Conflict,
    };
}

/// <summary>设备注册文件 <c>devices/&lt;id&gt;.json</c>。</summary>
public sealed class DeviceDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("lastSeenAt")]
    public long LastSeenAt { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = "0.1";
}

public static class IndexSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string ToJson(IndexDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>解析失败返回 null（调用方据此走"重建索引"兜底，绝不崩）。</summary>
    public static IndexDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<IndexDocument>(json, Options);
            if (document is null)
            {
                return null;
            }

            document.Games = new Dictionary<string, IndexGameEntry>(
                document.Games ?? new Dictionary<string, IndexGameEntry>(),
                StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static string ToJson(DeviceDocument document) => JsonSerializer.Serialize(document, Options);

    public static DeviceDocument? TryParseDevice(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceDocument>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>本地对某个游戏"最后一次成功发布"的索引条目。</summary>
public sealed class LocalIndexEntry
{
    public Guid GameId { get; init; }

    public long Seq { get; init; }

    public Guid SnapshotId { get; init; }

    public string DeviceId { get; init; } = string.Empty;

    public long UpdatedAt { get; init; }

    public bool Conflict { get; init; }

    public IndexGameEntry ToEntry() => new()
    {
        Seq = Seq,
        SnapshotId = SnapshotId,
        DeviceId = DeviceId,
        UpdatedAt = UpdatedAt,
        Conflict = Conflict,
    };
}

/// <summary>一次合并里发现的冲突（供 UI 展示与统计）。</summary>
public sealed record IndexConflict(string GameId, IndexGameEntry Local, IndexGameEntry Remote, IndexGameEntry Winner);
