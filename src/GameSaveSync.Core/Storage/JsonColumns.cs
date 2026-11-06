using System.Text.Json;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Storage;

/// <summary>games / settings 的 JSON 列序列化（统一 camelCase，与 SQLite 中的列名一致）。</summary>
public static class JsonColumns
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    public static string SerializeSaveRoots(IEnumerable<SaveRoot> roots) =>
        Serialize(roots.ToList());

    public static List<SaveRoot> DeserializeSaveRoots(string? json) =>
        Deserialize<List<SaveRoot>>(json) ?? new List<SaveRoot>();

    public static string SerializeStringList(IEnumerable<string> values) =>
        Serialize(values.ToList());

    public static List<string> DeserializeStringList(string? json) =>
        Deserialize<List<string>>(json) ?? new List<string>();
}
