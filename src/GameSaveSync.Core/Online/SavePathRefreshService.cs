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
/// 「刷新存档路径」：重新抓取外部存档路径数据库的数据，并转成可用的
/// <see cref="SaveRoot"/> 列表。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>失败（网络/站点问题）时返回 <see cref="SavePathRefreshResult.Succeeded"/> = false，
/// 调用方据此保留原有存档根，绝不因为外部服务挂了就清空用户的配置。</item>
/// <item>成功但数据源里没有记录时，视为"没查到"，同样不动原有数据。</item>
/// <item>依赖 <c>{installDir}</c> 的模板在游戏没填安装目录时无法定位，会被跳过并计数。</item>
/// <item>支持多个数据源依次回退：PCGamingWiki 容易被 Cloudflare 拦，
/// 清单型数据源（走 GitHub Raw）作为替补，成功即返回。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SavePathRefreshService
{
    private readonly IReadOnlyList<IOnlineSavePathProvider> _providers;
    private readonly IPathTokenResolver _tokens;

    public SavePathRefreshService(
        IEnumerable<IOnlineSavePathProvider> providers,
        IPathTokenResolver tokens)
    {
        var list = (providers ?? throw new ArgumentNullException(nameof(providers))).ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("至少需要一个存档路径数据源。", nameof(providers));
        }

        _providers = list;
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>
    /// 抓取并转换存档路径。<paramref name="installDir"/> 用于解析 <c>{installDir}</c> 模板。
    /// </summary>
    /// <param name="gameName">游戏名。</param>
    /// <param name="installDir">游戏安装目录（可空）。</param>
    /// <param name="externalId">平台标识（如 Steam AppID），用于精确匹配。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="progress">进度回调（用于在界面上提示"正在下载存档路径数据库…"）。</param>
    public async Task<SavePathRefreshResult> RefreshAsync(
        string gameName,
        string? installDir,
        CancellationToken ct,
        string? externalId = null,
        Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return new SavePathRefreshResult(false, Array.Empty<SaveRoot>(), 0, 0, "游戏名称为空。", null);
        }

        var failures = new List<string>();
        string? lastSource = null;

        foreach (var provider in _providers)
        {
            progress?.Invoke($"正在查询 {Describe(provider)}…");

            OnlineSavePathQueryResult query;
            try
            {
                query = await provider
                    .QueryDetailedAsync(gameName.Trim(), externalId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{Describe(provider)}：{ex.Message}");
                continue;
            }

            if (!query.Succeeded)
            {
                failures.Add($"{Describe(provider)}：{query.ErrorMessage ?? "未知错误"}");
                continue;
            }

            lastSource = query.SourceUrl;

            if (query.Paths.Count == 0)
            {
                // 这个源能访问但没有这个游戏的记录 —— 换下一个源再试
                failures.Add($"{Describe(provider)}：数据源里没有该游戏的记录");
                continue;
            }

            return BuildResult(query, installDir);
        }

        // 所有源都没给出可用结果
        var reason = failures.Count == 0 ? "没有可用的存档路径数据源" : string.Join("；", failures);
        return new SavePathRefreshResult(
            false,
            Array.Empty<SaveRoot>(),
            0,
            0,
            $"查询失败：{reason}。保留原有配置。",
            lastSource);
    }

    private SavePathRefreshResult BuildResult(OnlineSavePathQueryResult query, string? installDir)
    {
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

    private static string Describe(IOnlineSavePathProvider provider) => provider switch
    {
        LudusaviManifestProvider => "存档路径清单",
        PcGamingWikiProvider => "PCGamingWiki",
        _ => provider.GetType().Name,
    };
}
