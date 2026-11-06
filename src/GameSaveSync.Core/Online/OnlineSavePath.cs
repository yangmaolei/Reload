namespace GameSaveSync.Core.Online;

/// <summary>在线查到的已知存档路径（来源：PCGamingWiki 等社区数据库）。</summary>
public sealed record OnlineSavePath(
    string GameName,
    string PathTemplate,
    OnlineSavePathKind Kind,
    string SourceUrl);

public enum OnlineSavePathKind
{
    /// <summary>存档本体（Save game data location）。</summary>
    Saves = 0,

    /// <summary>配置文件（Configuration file location）。</summary>
    Config = 1,
}

/// <summary>
/// 一次在线查询的完整结果。
/// 区分「查询失败（网络/站点问题）」与「查询成功但没有结果」——两者的处理方式完全不同：
/// 前者要提示错误并保留原数据，后者只是"这个游戏网上没有记录"。
/// </summary>
public sealed record OnlineSavePathQueryResult(
    bool Succeeded,
    IReadOnlyList<OnlineSavePath> Paths,
    string? ErrorMessage,
    string? SourceUrl)
{
    public static OnlineSavePathQueryResult Ok(IReadOnlyList<OnlineSavePath> paths, string? sourceUrl = null) =>
        new(true, paths, null, sourceUrl ?? paths.FirstOrDefault()?.SourceUrl);

    public static OnlineSavePathQueryResult Fail(string errorMessage) =>
        new(false, Array.Empty<OnlineSavePath>(), errorMessage, null);
}

/// <summary>
/// 按游戏名查询网上的已知存档路径。
/// 纯装饰/辅助能力：失败必须静默降级，绝不影响备份主流程。
/// </summary>
public interface IOnlineSavePathProvider
{
    /// <summary>查询；无结果或网络异常返回空列表。</summary>
    Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct);

    /// <summary>
    /// 带状态明细的查询。失败时 <see cref="OnlineSavePathQueryResult.Succeeded"/> 为 false，
    /// 并在 <see cref="OnlineSavePathQueryResult.ErrorMessage"/> 里给出原因。
    /// </summary>
    Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, CancellationToken ct);

    /// <summary>
    /// 带平台标识（如 Steam AppID）的查询。
    /// 有 AppID 的数据源可以用它做精确匹配，避免同名游戏匹配错；
    /// 不支持精确匹配的实现可以忽略这个参数。
    /// </summary>
    Task<OnlineSavePathQueryResult> QueryDetailedAsync(string gameName, string? externalId, CancellationToken ct) =>
        QueryDetailedAsync(gameName, ct);
}
