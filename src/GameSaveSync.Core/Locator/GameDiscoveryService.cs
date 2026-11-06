using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Storage;

namespace GameSaveSync.Core.Locator;

public sealed class DiscoveryOptions
{
    public bool IncludeSteam { get; init; } = true;

    public bool IncludeEpic { get; init; } = true;

    public bool IncludeGog { get; init; } = true;

    public bool IncludeUbisoft { get; init; } = true;

    /// <summary>
    /// 包含 Xbox / 微软商店游戏（MSIX 游戏包 + Xbox 原生游戏库）。
    /// </summary>
    public bool IncludeXbox { get; init; } = true;

    /// <summary>只返回已经自动找到存档目录的游戏（UI 默认勾选这些）。</summary>
    public bool OnlyWithSaveRoots { get; init; }
}

/// <summary>
/// 把各平台定位器的结果合成一份"可勾选清单"：去重、跑规则库补存档根、标记已入库。
/// 扫描过程不写数据库，只有用户确认后由 <see cref="CreateGame"/> 生成 Game。
/// </summary>
public sealed class GameDiscoveryService
{
    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;
    private readonly IPathTokenResolver _tokens;
    private readonly IGameRepository _games;
    private readonly IAppLogger _logger;
    private readonly SavePathRuleSet _rules;

    public GameDiscoveryService(
        IFileSystem fileSystem,
        ILocatorEnvironment environment,
        IPathTokenResolver tokens,
        IGameRepository games,
        IAppLogger logger,
        SavePathRuleSet? rules = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rules = rules ?? SavePathRuleSet.BuiltIn;
    }

    public static SavePathRuleSet LoadRules(IFileSystem fileSystem, AppPaths paths, IAppLogger logger)
    {
        var ruleSet = SavePathRuleSet.Load(
            Path.Combine(paths.DataRoot, SavePathRuleSet.OverrideFileName), fileSystem, out var warning);

        if (warning is not null)
        {
            logger.Warn(warning);
        }

        return ruleSet;
    }

    public async Task<DiscoveryResult> ScanAsync(DiscoveryOptions? options, CancellationToken ct)
    {
        options ??= new DiscoveryOptions();
        var warnings = new List<string>();
        var games = new List<DiscoveredGame>();
        var libraryCount = 0;

        if (options.IncludeSteam)
        {
            var found = new SteamLocator(_fileSystem, _environment).Scan(out var issues);
            games.AddRange(found);
            warnings.AddRange(issues);
            libraryCount += found.Count;
        }

        if (options.IncludeEpic)
        {
            var found = new EpicLocator(_fileSystem, _environment).Scan(out var issues);
            games.AddRange(found);
            warnings.AddRange(issues);
            libraryCount += found.Count;
        }

        if (options.IncludeGog)
        {
            var found = new GogLocator(_fileSystem, _environment).Scan(out var issues);
            games.AddRange(found);
            warnings.AddRange(issues);
            libraryCount += found.Count;
        }

        if (options.IncludeUbisoft)
        {
            var found = new UbisoftLocator(_fileSystem, _environment).Scan(out var issues);
            games.AddRange(found);
            warnings.AddRange(issues);
            libraryCount += found.Count;
        }

        if (options.IncludeXbox)
        {
            var found = new XboxLocator(_fileSystem, _environment).Scan(out var issues);
            games.AddRange(found);
            warnings.AddRange(issues);
            libraryCount += found.Count;
        }

        ct.ThrowIfCancellationRequested();

        var resolver = new SavePathResolver(_fileSystem, _tokens, _rules);
        var deduped = new List<DiscoveredGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var key = !string.IsNullOrWhiteSpace(game.InstallDir)
                ? $"dir:{game.InstallDir.TrimEnd('\\', '/').ToLowerInvariant()}"
                : $"{game.Source}:{game.Name}".ToLowerInvariant();

            if (!seen.Add(key))
            {
                continue;
            }

            var roots = resolver.ResolveRoots(game);
            game.SuggestedRoots.Clear();
            game.SuggestedRoots.AddRange(roots);

            if (game.SuggestedRoots.Count == 0)
            {
                game.Notes.Add("没找到存档目录，可用「探测模式」在游戏运行后自动比对");
            }

            if (options.OnlyWithSaveRoots && game.SuggestedRoots.Count == 0)
            {
                continue;
            }

            deduped.Add(game);
        }

        var existing = await _games.ListAsync(ct).ConfigureAwait(false);
        foreach (var game in deduped)
        {
            game.AlreadyAdded = existing.Any(e =>
                (!string.IsNullOrWhiteSpace(e.InstallDir) && !string.IsNullOrWhiteSpace(game.InstallDir) &&
                 string.Equals(e.InstallDir.TrimEnd('\\'), game.InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) ||
                (e.ExternalId is not null && game.ExternalId is not null &&
                 e.ExternalId.Equals(game.ExternalId, StringComparison.OrdinalIgnoreCase)) ||
                e.Name.Equals(game.Name, StringComparison.OrdinalIgnoreCase));
        }

        deduped.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        _logger.Info($"平台扫描完成：{deduped.Count} 个游戏（含 {libraryCount} 个平台条目）");

        return new DiscoveryResult(deduped, libraryCount, warnings);
    }

    /// <summary>把用户勾选的候选转成可入库的 Game；存档根在这里完成令牌化。</summary>
    public Game CreateGame(DiscoveredGame discovered, IEnumerable<string> saveRoots, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        var game = new Game
        {
            Name = discovered.Name.Trim(),
            Source = discovered.Source,
            ExternalId = discovered.ExternalId,
            InstallDir = string.IsNullOrWhiteSpace(discovered.InstallDir) ? null : discovered.InstallDir.Trim(),
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
        };

        game.ExeNames.AddRange(discovered.ExeNames);

        foreach (var root in saveRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var saveRoot = SaveRootResolver.CreateSaveRoot(root, _tokens);
            game.SaveRoots.Add(saveRoot);

            if (!saveRoot.IsTokenized)
            {
                _logger.Warn($"目录不在标准位置，只能以绝对路径保存（跨机需要重新定位）：{root}");
            }
        }

        return game;
    }
}
