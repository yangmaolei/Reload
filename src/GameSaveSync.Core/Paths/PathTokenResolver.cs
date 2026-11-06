using System.Text;

namespace GameSaveSync.Core.Paths;

/// <summary>令牌无法在本机展开（例如跨机器后存档根不存在）。</summary>
public sealed class PathTokenException : Exception
{
    public PathTokenException(string token, string message) : base(message) => Token = token;

    public string Token { get; }
}

/// <summary>
/// 令牌化路径的展开与反向令牌化。
/// 支持的令牌：%USERPROFILE% %APPDATA% %LOCALAPPDATA% %DOCUMENTS% %SAVEDGAMES%
/// （M4 再加入 %GAMEDIR% &lt;SteamUserData&gt; &lt;SteamAppId&gt; 等）。
/// </summary>
public interface IPathTokenResolver
{
    /// <summary>展开所有令牌；有任何令牌在本机不存在则抛 <see cref="PathTokenException"/>。</summary>
    string Expand(string tokenizedPath);

    /// <summary>逐段展开，缺失的令牌保留原样以便上报（不抛异常）。</summary>
    bool TryExpand(string tokenizedPath, out string expandedPath, out string? missingToken);

    /// <summary>把本机绝对路径反向令牌化；不在任何已知根下返回 null。</summary>
    string? Tokenize(string absolutePath);

    /// <summary>本机令牌取值（只读快照，测试可注入）。</summary>
    IReadOnlyDictionary<string, string> Tokens { get; }
}

public sealed class PathTokenResolver : IPathTokenResolver
{
    public const string UserProfile = "%USERPROFILE%";
    public const string AppData = "%APPDATA%";
    public const string LocalAppData = "%LOCALAPPDATA%";
    public const string Documents = "%DOCUMENTS%";
    public const string SavedGames = "%SAVEDGAMES%";

    private static readonly string[] TokenOrder =
    {
        LocalAppData, AppData, UserProfile, Documents, SavedGames,
    };

    private readonly Dictionary<string, string> _tokens;

    public PathTokenResolver() : this(BuildDefaultTokens())
    {
    }

    public PathTokenResolver(IDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in tokens)
        {
            var normalized = Normalize(key);
            if (!string.IsNullOrEmpty(value))
            {
                _tokens[normalized] = TrimTrailingSeparator(Path.GetFullPath(value));
            }
        }
    }

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public static Dictionary<string, string> BuildDefaultTokens()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = Path.Combine(profile, "Documents");
        }

        // Saved Games 是已知文件夹（FOLDERID_SavedGames），没有 SpecialFolder 枚举值，
        // 但位置在 .NET 上固定为 %USERPROFILE%\Saved Games。
        var savedGames = Path.Combine(profile, "Saved Games");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string token, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[token] = value;
            }
        }

        Add(UserProfile, profile);
        Add(AppData, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(LocalAppData, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Documents, documents);
        Add(SavedGames, savedGames);
        return map;
    }

    public string Expand(string tokenizedPath)
    {
        if (string.IsNullOrWhiteSpace(tokenizedPath))
        {
            throw new ArgumentException("路径为空", nameof(tokenizedPath));
        }

        var expanded = ExpandCore(tokenizedPath, out var missing);
        if (missing is not null)
        {
            throw new PathTokenException(missing, $"令牌 {missing} 在本机不存在，无法解析路径：{tokenizedPath}");
        }

        return expanded;
    }

    public bool TryExpand(string tokenizedPath, out string expandedPath, out string? missingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizedPath);
        expandedPath = ExpandCore(tokenizedPath, out missingToken);
        return missingToken is null;
    }

    public string? Tokenize(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        var full = TrimTrailingSeparator(Path.GetFullPath(absolutePath));

        string? bestToken = null;
        var bestLength = -1;

        foreach (var token in TokenOrder)
        {
            if (!_tokens.TryGetValue(token, out var root) || root.Length == 0)
            {
                continue;
            }

            if (!IsUnder(full, root))
            {
                continue;
            }

            if (root.Length > bestLength)
            {
                bestLength = root.Length;
                bestToken = token;
            }
        }

        // 兜底：允许任意自定义令牌参与最长前缀匹配
        foreach (var (token, root) in _tokens)
        {
            if (root.Length > bestLength && IsUnder(full, root))
            {
                bestLength = root.Length;
                bestToken = token;
            }
        }

        if (bestToken is null)
        {
            return null;
        }

        var remainder = full[bestLength..].TrimStart('\\', '/');
        return remainder.Length == 0 ? bestToken : $"{bestToken}\\{remainder}";
    }

    private string ExpandCore(string tokenizedPath, out string? missingToken)
    {
        missingToken = null;
        var builder = new StringBuilder(tokenizedPath.Length + 32);
        var index = 0;

        while (index < tokenizedPath.Length)
        {
            var percent = tokenizedPath.IndexOf('%', index);
            if (percent < 0)
            {
                builder.Append(tokenizedPath, index, tokenizedPath.Length - index);
                break;
            }

            builder.Append(tokenizedPath, index, percent - index);

            var closing = tokenizedPath.IndexOf('%', percent + 1);
            if (closing < 0)
            {
                builder.Append(tokenizedPath, percent, tokenizedPath.Length - percent);
                break;
            }

            var token = tokenizedPath.Substring(percent, closing - percent + 1);

            if (_tokens.TryGetValue(Normalize(token), out var value))
            {
                builder.Append(value);
            }
            else if (IsKnownTokenShape(token))
            {
                missingToken ??= token;
                builder.Append(token);
            }
            else
            {
                builder.Append(token);
            }

            index = closing + 1;
        }

        // 令牌值在入库时已去掉尾部分隔符，展开后不会出现重复分隔符（UNC 前缀保持原样）
        return builder.ToString();
    }

    private static bool IsKnownTokenShape(string token) =>
        token.Length > 2 && token[0] == '%' && token[^1] == '%';

    private static string Normalize(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length > 0 && trimmed[0] != '%')
        {
            trimmed = "%" + trimmed;
        }

        if (trimmed.Length > 0 && trimmed[^1] != '%')
        {
            trimmed += "%";
        }

        return trimmed.ToUpperInvariant();
    }

    private static string TrimTrailingSeparator(string path) =>
        path.Length > 3 ? path.TrimEnd('\\', '/') : path;

    private static bool IsUnder(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate.Length > root.Length && (candidate[root.Length] == '\\' || candidate[root.Length] == '/');
    }
}
