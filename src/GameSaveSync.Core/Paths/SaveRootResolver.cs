using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Paths;

public sealed record ResolvedSaveRoot(int Index, SaveRoot Root, string? AbsolutePath, bool DirectoryExists);

/// <summary>一组存档根的解析结果。缺令牌或目录不存在时 <see cref="IsComplete"/> 为 false。</summary>
public sealed class SaveRootResolution
{
    public SaveRootResolution(
        IReadOnlyList<ResolvedSaveRoot> roots,
        IReadOnlyList<string> missingTokens)
    {
        Roots = roots;
        MissingTokens = missingTokens;
    }

    public IReadOnlyList<ResolvedSaveRoot> Roots { get; }

    /// <summary>本机无法展开的令牌（去重）。</summary>
    public IReadOnlyList<string> MissingTokens { get; }

    /// <summary>令牌缺失或目录不存在的根（"需重新定位"）。</summary>
    public IReadOnlyList<ResolvedSaveRoot> UnavailableRoots =>
        Roots.Where(r => r.AbsolutePath is null || !r.DirectoryExists).ToArray();

    public bool IsComplete => MissingTokens.Count == 0 && UnavailableRoots.Count == 0;
}

public static class SaveRootResolver
{
    /// <summary>展开游戏的所有存档根，绝不猜测路径。</summary>
    public static SaveRootResolution Resolve(Game game, IPathTokenResolver resolver, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var resolved = new List<ResolvedSaveRoot>(game.SaveRoots.Count);
        var missing = new List<string>();

        for (var i = 0; i < game.SaveRoots.Count; i++)
        {
            var root = game.SaveRoots[i];

            // M3：用户为该存档根指定的本机目录优先于令牌展开（跨机"重新定位"）
            if (game.PathOverrides.TryGetValue(root.Token, out var overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                var exists = fileSystem.DirectoryExists(overridePath);
                resolved.Add(new ResolvedSaveRoot(i, root, overridePath, exists));
                continue;
            }

            if (!root.IsTokenized)
            {
                // 用户选的目录不在任何标准根下：M1 允许存绝对路径，但跨机必然需要重新定位
                var exists = !string.IsNullOrWhiteSpace(root.Token) && fileSystem.DirectoryExists(root.Token);
                resolved.Add(new ResolvedSaveRoot(i, root, string.IsNullOrWhiteSpace(root.Token) ? null : root.Token, exists));
                continue;
            }

            if (resolver.TryExpand(root.Token, out var expanded, out var missingToken) && missingToken is null)
            {
                resolved.Add(new ResolvedSaveRoot(i, root, expanded, fileSystem.DirectoryExists(expanded)));
            }
            else
            {
                if (missingToken is not null && !missing.Contains(missingToken))
                {
                    missing.Add(missingToken);
                }

                resolved.Add(new ResolvedSaveRoot(i, root, null, false));
            }
        }

        return new SaveRootResolution(resolved, missing);
    }

    /// <summary>把用户选择的绝对目录转成令牌化路径；无法令牌化时返回绝对路径并标记 IsTokenized=false。</summary>
    public static SaveRoot CreateSaveRoot(string absoluteOrTokenizedPath, IPathTokenResolver resolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteOrTokenizedPath);
        ArgumentNullException.ThrowIfNull(resolver);

        var trimmed = absoluteOrTokenizedPath.Trim().TrimEnd('\\', '/');
        if (trimmed.Contains('%'))
        {
            return new SaveRoot { Token = trimmed, IsTokenized = true };
        }

        var tokenized = resolver.Tokenize(trimmed);
        return tokenized is null
            ? new SaveRoot { Token = trimmed, IsTokenized = false }
            : new SaveRoot { Token = tokenized, IsTokenized = true };
    }
}
