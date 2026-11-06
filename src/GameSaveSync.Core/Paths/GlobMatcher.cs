using System.Text;
using System.Text.RegularExpressions;

namespace GameSaveSync.Core.Paths;

/// <summary>
/// SaveRoot 的 include / exclude 通配符匹配器。
/// 语义（Windows 语义、大小写不敏感、分隔符统一为 \）：
/// <list type="bullet">
/// <item><c>*.log</c>：不含分隔符 → 匹配任意层级的文件名。</item>
/// <item><c>cache/*</c>：含分隔符 → 匹配任意层级下的 cache 子目录内容。</item>
/// <item><c>**</c>：跨目录。</item>
/// <item><c>?</c>：单个非分隔符字符。</item>
/// </list>
/// 未提供 include 时视为"全部包含"；exclude 优先于 include。
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;

    public GlobMatcher(IEnumerable<string>? include, IEnumerable<string>? exclude)
    {
        _include = Compile(include);
        _exclude = Compile(exclude);
    }

    public static GlobMatcher All { get; } = new(null, null);

    public bool IsMatch(string relativePath)
    {
        var normalized = Normalize(relativePath);

        foreach (var regex in _exclude)
        {
            if (regex.IsMatch(normalized))
            {
                return false;
            }
        }

        if (_include.Count == 0)
        {
            return true;
        }

        foreach (var regex in _include)
        {
            if (regex.IsMatch(normalized))
            {
                return true;
            }
        }

        return false;
    }

    public static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>单次匹配单个通配符（规则库按游戏名筛选时用）。</summary>
    public static bool IsMatchPattern(string pattern, string value) =>
        string.IsNullOrWhiteSpace(pattern) || new GlobMatcher(new[] { pattern }, null).IsMatch(value);

    private static List<Regex> Compile(IEnumerable<string>? patterns)
    {
        var result = new List<Regex>();
        if (patterns is null)
        {
            return result;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var body = Normalize(pattern.Trim());
            var regex = new StringBuilder("^");

            // 语义：任意层级的同名文件/目录都算命中（用户直觉：*.log 表示所有 .log）。
            // 以 ** 开头的模式自带跨目录语义，不再加前缀。
            if (!body.StartsWith("**", StringComparison.Ordinal))
            {
                regex.Append("(?:.*\\\\)?");
            }

            regex.Append(GlobToRegex(body));
            regex.Append('$');

            result.Add(new Regex(
                regex.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2)));
        }

        return result;
    }

    private static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder(glob.Length * 2);

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '\\')
                        {
                            i++;
                            builder.Append("(?:.*\\\\)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^\\\\]*");
                    }

                    break;

                case '?':
                    builder.Append("[^\\\\]");
                    break;

                case '\\':
                    builder.Append("\\\\");
                    break;

                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}
