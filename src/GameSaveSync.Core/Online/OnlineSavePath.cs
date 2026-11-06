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
/// 按游戏名查询网上的已知存档路径。
/// 纯装饰/辅助能力：失败必须静默降级，绝不影响备份主流程。
/// </summary>
public interface IOnlineSavePathProvider
{
    /// <summary>查询；无结果或网络异常返回空列表。</summary>
    Task<IReadOnlyList<OnlineSavePath>> QueryAsync(string gameName, CancellationToken ct);
}
