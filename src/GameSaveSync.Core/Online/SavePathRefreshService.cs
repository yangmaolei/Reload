using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;

namespace GameSaveSync.Core.Online;

/// <summary>刷新存档路径的结果。</summary>
public sealed record SavePathRefreshResult(
    bool Succeeded,
    IReadOnlyList<SaveRoot> Roots,
    int AddedCount,
    int RemovedCount,
    string Message,
    string? SourceUrl);

/// <summary>
/// 「刷新存档路径」：按游戏名重新抓取外部存档路径站点（PCGamingWiki）的数据，
/// 并把结果转成可用的 <see cref="SaveRoot"/> 列表。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>失败（网络/站点问题）时返回 <see cref="SavePathRefreshResult.Succeeded"/> = false，
/// 调用方据此保留原有存档根，绝不因为外部服务挂了就清空用户的配置。</item>
/// <item>成功但站内没有记录时，视为"没查到"，同样不动原有数据。</item>
/// <item>依赖 <c>{installDir}</c> 的模板在游戏没填安装目录时无法定位，会被跳过并计数。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SavePathRefreshService
{
    private readonly IOnlineSavePathProvider _provider;
    private readonly IPathTokenResolver _tokens;

    public SavePathRefreshService(IOnlineSavePathProvider provider, IPathTokenResolver tokens)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// 抓取并转换存档路径。<paramref name="installDir"/> 用于解析 <c>{installDir}</c> 模板。
    /// </summary>
    public async Task<SavePathRefreshResult> RefreshAsync(
        string gameName,
        string? installDir,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return new SavePathRefreshResult(false, Array.Empty<SaveRoot>(), 0, 0, "游戏名称为空。", null);
        }

        OnlineSavePathQueryResult query;
        try
        {
            query = await _provider.QueryDetailedAsync(gameName.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SavePathRefreshResult(false, Array.Empty<SaveRoot>(), 0, 0, $"查询失败：{ex.Message}", null);
        }

        if (!query.Succeeded)
        {
            // 外部服务不可用：保留原有数据，把错误原因交回去
            return new SavePathRefreshResult(
                false,
                Array.Empty<SaveRoot>(),
                0,
                0,
                $"查询失败：{query.ErrorMessage ?? "未知错误"}",
                query.SourceUrl);
        }

        if (query.Paths.Count == 0)
        {
            return new SavePathRefreshResult(
                false,
                Array.Empty<SaveRoot>(),
                0,
                0,
                "站内没有该游戏的存档路径记录，保留原有配置。",
                query.SourceUrl);
        }

        var roots = new List<SaveRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var path in query.Paths)
        {
            var token = ToToken(path.PathTemplate, installDir);
            if (token is null)
            {
                skipped++;
                continue;
            }

            if (!seen.Add(token))
            {
                continue;
            }

            roots.Add(SaveRootResolver.CreateSaveRoot(token, _tokens));
        }

        if (roots.Count == 0)
        {
            return new SavePathRefreshResult(
                false,
                Array.Empty<SaveRoot>(),
                0,
                0,
                skipped > 0
                    ? $"查到 {query.Paths.Count} 条，但都依赖安装目录（当前未填写），保留原有配置。"
                    : "查到的路径都无法解析，保留原有配置。",
                query.SourceUrl);
        }

        return new SavePathRefreshResult(
            true,
            roots,
            roots.Count,
            0,
            skipped > 0
                ? $"刷新成功：{roots.Count} 个存档目录（另有 {skipped} 条依赖安装目录已跳过）。"
                : $"刷新成功：{roots.Count} 个存档目录。",
            query.SourceUrl);
    }

    /// <summary>
    /// 把外部模板转成令牌化路径。
    /// <c>{installDir}</c> 在没有安装目录时无法定位，返回 null。
    /// </summary>
    private static string? ToToken(string template, string? installDir)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var trimmed = template.Trim().TrimEnd('\\', '/');

        if (trimmed.Contains("{installDir}", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return null;
            }

            trimmed = trimmed.Replace(
                "{installDir}",
                installDir.Trim().TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        // 其它未识别的占位符一律跳过（不能凭空造路径）
        if (trimmed.Contains('{') || trimmed.Contains('}'))
        {
            return null;
        }

        return trimmed.Length == 0 ? null : trimmed;
    }
}
