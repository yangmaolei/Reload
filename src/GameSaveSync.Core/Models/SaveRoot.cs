using System.Text.Json.Serialization;

namespace GameSaveSync.Core.Models;

/// <summary>
/// 一个存档根目录。<see cref="Token"/> 必须是"令牌 + 相对路径"形式（如
/// <c>%APPDATA%\Studio\Game\Saves</c>），跨机器同步时用本机值重新展开。
/// </summary>
public sealed class SaveRoot
{
    /// <summary>令牌化路径，禁止存本机绝对路径（除非 <see cref="IsTokenized"/> 为 false 的兜底情形）。</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>该路径是否已成功令牌化。false 表示用户选择的目录不在任何标准根下，只能存绝对路径（会在 UI 提示跨机失效）。</summary>
    [JsonPropertyName("isTokenized")]
    public bool IsTokenized { get; set; } = true;

    /// <summary>包含通配符，空表示全部包含。支持 <c>*.sav</c>、<c>**/*.sav</c>、<c>saves/*</c>。</summary>
    [JsonPropertyName("include")]
    public List<string> Include { get; set; } = new();

    /// <summary>排除通配符，优先级高于 include。如 <c>*.log</c>、<c>cache/*</c>。</summary>
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = new();

    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; } = true;

    public SaveRoot Clone() => new()
    {
        Token = Token,
        IsTokenized = IsTokenized,
        Include = new List<string>(Include),
        Exclude = new List<string>(Exclude),
        Recursive = Recursive,
    };
}

/// <summary>GFS 保留策略。M1 仅存储，不执行清理（M2 实现）。</summary>
public sealed class RetentionPolicy
{
    [JsonPropertyName("keepLast")]
    public int KeepLast { get; set; } = 20;

    [JsonPropertyName("daily")]
    public int Daily { get; set; } = 7;

    [JsonPropertyName("weekly")]
    public int Weekly { get; set; } = 4;
}
