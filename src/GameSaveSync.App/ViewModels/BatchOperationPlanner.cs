using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Export;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.App.ViewModels;

/// <summary>
/// 批量操作的"业务决策层"：把"选中了哪些游戏 → 该做什么 → 结果怎么算"从 UI 里剥出来。
/// <para>
/// ViewModel 只负责弹窗与状态展示，判定逻辑全在这里，这样能单测而不必拉起整个 WPF 应用。
/// </para>
/// </summary>
public sealed class BatchOperationPlanner
{
    private readonly IGameRepository _games;
    private readonly IBatchSaveExporter _exporter;
    private readonly IAppLogger _logger;

    public BatchOperationPlanner(
        IGameRepository games,
        IBatchSaveExporter exporter,
        IAppLogger logger)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>没有选中任何游戏时的提示。</summary>
    public const string NothingSelectedMessage = "请先在列表里勾选要操作的游戏。";

    /// <summary>
    /// 把勾选的游戏导出到目录。
    /// </summary>
    public async Task<BatchExportResult> BackupToDirectoryAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationRoot,
        bool overwriteExisting,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gameIds);

        if (gameIds.Count == 0)
        {
            return BatchExportResult.Nothing;
        }

        var result = await _exporter
            .ExportToDirectoryAsync(gameIds, destinationRoot, overwriteExisting, ct)
            .ConfigureAwait(false);

        _logger.Info($"批量备份：{result.Summary} → {result.OutputPath}");
        return result;
    }

    /// <summary>把勾选的游戏打包成 zip。</summary>
    public async Task<BatchExportResult> ExportToZipAsync(
        IReadOnlyList<Guid> gameIds,
        string destinationZipPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gameIds);

        if (gameIds.Count == 0)
        {
            return BatchExportResult.Nothing;
        }

        var result = await _exporter
            .ExportToZipAsync(gameIds, destinationZipPath, ct)
            .ConfigureAwait(false);

        _logger.Info($"批量导出：{result.Summary} → {result.OutputPath}");
        return result;
    }

    /// <summary>
    /// 校验勾选的游戏是否还有效（游戏是否还在库里）。
    /// 用于在真正开跑前给用户一个明确的提示，而不是让导出过程半途失败。
    /// </summary>
    public async Task<IReadOnlyList<string>> FindMissingGamesAsync(
        IReadOnlyList<Guid> gameIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gameIds);

        var missing = new List<string>();
        foreach (var id in gameIds.Distinct())
        {
            var game = await _games.GetAsync(id, ct).ConfigureAwait(false);
            if (game is null)
            {
                missing.Add(id.ToString("N")[..8]);
            }
        }

        return missing;
    }
}
