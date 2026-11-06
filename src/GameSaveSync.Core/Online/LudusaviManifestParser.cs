namespace GameSaveSync.Core.Online;

/// <summary>
/// 从 Ludusavi Manifest 的游戏块里提取 Windows 存档/配置路径。
/// <para>
/// 清单里的路径用尖括号占位符表示（<c>&lt;winLocalAppData&gt;</c>、
/// <c>&lt;home&gt;</c> 等），这里把它们换成本项目自己的令牌
/// （<c>%LOCALAPPDATA%</c>、<c>%USERPROFILE%</c> …），并把文件路径
/// 收敛成"存档根目录"——因为本项目备份的粒度是目录。
/// </para>
/// </summary>
public static class LudusaviManifestParser
{
    /// <summary>占位符 → 本项目令牌的映射。</summary>
    private static readonly (string Placeholder, string Token)[] TokenMap =
    {
        ("<winLocalAppDataLow>", "%LOCALAPPDATA%\\Low"),
        ("<winLocalAppData>", "%LOCALAPPDATA%"),
        ("<winAppData>", "%APPDATA%"),
        ("<winDocuments>", "%DOCUMENTS%"),
        ("<winPublic>", "%PUBLIC%"),
        ("<winProgramData>", "%PROGRAMDATA%"),
        ("<home>", "%USERPROFILE%"),
        ("<winDir>", "%WINDIR%"),
    };

    /// <summary>
    /// 提取块里的 Windows 路径。
    /// </summary>
    /// <param name="blockText">游戏块的内容（不含顶层键行）。</param>
    /// <param name="gameName">游戏名，用于回填 <see cref="OnlineSavePath.GameName"/>。</param>
    /// <param name="sourceUrl">来源链接。</param>
    public static IReadOnlyList<OnlineSavePath> ExtractWindowsPaths(
        string blockText,
        string gameName,
        string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(blockText))
        {
            return Array.Empty<OnlineSavePath>();
        }

        var lines = blockText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var results = new List<OnlineSavePath>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var inFiles = false;
        string? currentPath = null;
        var currentTags = new List<string>();
        var currentWhen = new List<string>();

        void Flush()
        {
            if (currentPath is null)
            {
                return;
            }

            var path = currentPath;
            var tags = currentTags.ToList();
            var when = currentWhen.ToList();
            currentPath = null;
            currentTags.Clear();
            currentWhen.Clear();

            if (!IsWindowsApplicable(when))
            {
                return;
            }

            var token = ToToken(path);
            if (token is null)
            {
                return;
            }

            // tags 里带 save 的当存档本体，带 config 的当配置文件；
            // 两者都没有时按存档目录处理（清单里没标 tag 的条目通常是整目录）
            var isSave = tags.Any(t => t.Equals("save", StringComparison.OrdinalIgnoreCase));
            var isConfig = tags.Any(t => t.Equals("config", StringComparison.OrdinalIgnoreCase));

            if (!isSave && !isConfig)
            {
                isSave = true;
            }

            foreach (var kind in new[] { isSave ? OnlineSavePathKind.Saves : (OnlineSavePathKind?)null, isConfig ? OnlineSavePathKind.Config : (OnlineSavePathKind?)null })
            {
                if (kind is null)
                {
                    continue;
                }

                var key = $"{kind.Value}|{token}";
                if (!seen.Add(key))
                {
                    continue;
                }

                results.Add(new OnlineSavePath(gameName, token, kind.Value, sourceUrl));
            }
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var indent = CountIndent(line);
            var trimmed = line.Trim();

            if (indent == 0)
            {
                continue;
            }

            // files 段：缩进 2
            if (indent == 2)
            {
                Flush();
                inFiles = trimmed.Equals("files:", StringComparison.Ordinal);
                continue;
            }

            if (!inFiles)
            {
                continue;
            }

            // 路径条目：缩进 4，形如 "<path>:" 或 <path>:
            if (indent == 4 && trimmed.EndsWith(':'))
            {
                Flush();
                var value = trimmed[..^1].Trim();
                currentPath = Unquote(value);
                continue;
            }

            if (currentPath is null)
            {
                continue;
            }

            // tags / when 段：缩进 6
            if (indent == 6)
            {
                if (trimmed.Equals("tags:", StringComparison.Ordinal))
                {
                    currentTags.Add("__tags__");
                }
                else if (trimmed.Equals("when:", StringComparison.Ordinal))
                {
                    currentWhen.Add("__when__");
                }

                continue;
            }

            // 列表项：缩进 8
            if (indent == 8 && trimmed.StartsWith('-'))
            {
                var item = trimmed[1..].Trim();
                if (currentTags.Count > 0 && currentTags[^1] == "__tags__")
                {
                    currentTags.Add(item);
                }
                else if (currentWhen.Count > 0 && currentWhen[^1] == "__when__")
                {
                    currentWhen.Add(item);
                }

                continue;
            }

            // 列表项的续行（如 "- os: windows" 下面的 "store: steam"）
            if (indent >= 10 && currentWhen.Count > 0 && currentWhen[^1] == "__when__")
            {
                currentWhen.Add(trimmed);
            }
        }

        Flush();

        return results;
    }

    /// <summary>
    /// 判断条件列表是否适用于 Windows。
    /// 没有 when 条件时默认适用；显式列了其它操作系统则不适用。
    /// </summary>
    private static bool IsWindowsApplicable(IReadOnlyList<string> when)
    {
        if (when.Count == 0)
        {
            return true;
        }

        var sawOs = false;
        foreach (var item in when)
        {
            if (item.StartsWith("os:", StringComparison.OrdinalIgnoreCase))
            {
                sawOs = true;
                var value = item[3..].Trim();
                if (!value.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        // 只写了 store 之类的限定条件而没写 os —— 对 Windows 仍然适用
        return sawOs || when.Count > 0;
    }

    /// <summary>
    /// 把清单里的路径转成本项目的令牌形式；无法定位的返回 null。
    /// </summary>
    public static string? ToToken(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = rawPath.Trim().Replace('/', '\\');

        // <root> 是"用户自定义的存档根"，<store> 是商店类型 —— 都无法静态定位
        if (path.Contains("<root>", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("<store>", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("<winDir>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // <storeUserId> 是每个账号不同的目录段，直接去掉取它的父目录
        path = RemoveSegment(path, "<storeUserId>");

        foreach (var (placeholder, token) in TokenMap)
        {
            path = path.Replace(placeholder, token, StringComparison.OrdinalIgnoreCase);
        }

        // <base> / <installDir> 指向游戏安装目录
        path = path.Replace("<base>", "{installDir}", StringComparison.OrdinalIgnoreCase);
        path = path.Replace("<installDir>", "{installDir}", StringComparison.OrdinalIgnoreCase);

        // 剩余无法识别的占位符一律放弃，不能凭空造路径
        if (path.Contains('<') || path.Contains('>'))
        {
            return null;
        }

        path = path.TrimEnd('\\');

        // 通配符路径：取到第一个 * 之前的目录
        var star = path.IndexOf('*');
        if (star >= 0)
        {
            var cut = path.LastIndexOf('\\', star);
            path = cut > 0 ? path[..cut] : string.Empty;
        }

        if (path.Length == 0)
        {
            return null;
        }

        // 指向具体文件的路径：取父目录（本项目按目录备份）
        var lastSeparator = path.LastIndexOf('\\');
        if (lastSeparator > 0)
        {
            var lastSegment = path[(lastSeparator + 1)..];
            if (lastSegment.Contains('.', StringComparison.Ordinal))
            {
                path = path[..lastSeparator];
            }
        }
        else if (path.Contains('.', StringComparison.Ordinal) && !path.Contains('%'))
        {
            // 只有一个文件名，没有目录 —— 无法定位
            return null;
        }

        return path.Length == 0 ? null : path;
    }

    /// <summary>去掉路径里等于指定占位符的那一段。</summary>
    private static string RemoveSegment(string path, string segment)
    {
        var parts = path.Split('\\', StringSplitOptions.None);
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!part.Equals(segment, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(part);
            }
        }

        return string.Join('\\', kept);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }
}
