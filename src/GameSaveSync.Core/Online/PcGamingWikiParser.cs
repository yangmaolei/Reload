using System.Text.RegularExpressions;

namespace GameSaveSync.Core.Online;

/// <summary>
/// PCGamingWiki 的 wikitext 解析器：从游戏页面的 <c>{{Game data/saves|...}}</c> /
/// <c>{{Game data/config|...}}</c> 模板里抽出已知存档路径，并把 <c>{{p|...}}</c> 令牌
/// 展开成我们自己的路径令牌（%USERPROFILE% 等），供 SaveRootResolver 直接使用。
/// </summary>
public static partial class PcGamingWikiParser
{
    /// <summary>PCGamingWiki 的 {{p|...}} 令牌 → 我们的路径令牌。</summary>
    private static readonly IReadOnlyDictionary<string, string> TokenMap = new Dictionary
        <string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["userprofile"] = "%USERPROFILE%",
        ["documents"] = "%DOCUMENTS%",
        ["savedgames"] = "%SAVEDGAMES%",
        ["appdata"] = "%APPDATA%",
        ["roaming"] = "%APPDATA%",
        ["localappdata"] = "%LOCALAPPDATA%",
        ["programdata"] = "%PROGRAMDATA%",
        ["game"] = "{installDir}",
        ["installpath"] = "{installDir}",
    };

    private static readonly char[] SlashChars = { '\\', '/' };

    private static string FirstSegment(string path)
    {
        var slash = path.IndexOfAny(SlashChars);
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>解析整个 wikitext，返回去重后的存档/配置路径模板。</summary>
    public static IReadOnlyList<OnlineSavePath> Parse(string wikitext, string gameName, string sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(wikitext);
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = "Unknown";
        }

        var result = new List<OnlineSavePath>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchFrom = 0;
        while (true)
        {
            var templateStart = wikitext.IndexOf("{{Game data/", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (templateStart < 0)
            {
                break;
            }

            // 用深度匹配找到这个模板真正的闭合 }}（{{p|...}} 里也有 }}，不能简单正则）
            var end = FindTemplateEnd(wikitext, templateStart);
            if (end < 0)
            {
                break;
            }

            var block = wikitext[templateStart..end];
            var kind = block.Contains("Game data/config", StringComparison.OrdinalIgnoreCase)
                ? OnlineSavePathKind.Config
                : OnlineSavePathKind.Saves;

            foreach (Match token in PathTokenRegex().Matches(block))
            {
                var raw = token.Groups["path"].Value.Trim();
                var path = Expand(raw);
                if (path is null)
                {
                    continue;
                }

                var key = $"{kind}:{path}";
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(new OnlineSavePath(gameName, path, kind, sourceUrl));
            }

            searchFrom = end + 2;
        }

        return result;
    }

    /// <summary>从 <paramref name="start"/> 处的 {{ 开始，返回与之配对的 }} 的起始索引。</summary>
    private static int FindTemplateEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (text[i] == '}' && text[i + 1] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }

                i++;
            }
        }

        return -1;
    }

    /// <summary>展开一个 {{p}} 内的路径：令牌替换 + 清理；注册表项等无法映射的返回 null。</summary>
    internal static string? Expand(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var path = rawPath.Trim();

        // 注册表项（hklm/hkcu/hkey_...）我们无法当文件路径用，跳过
        var firstSegment = FirstSegment(path);
        if (firstSegment.Equals("hklm", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.Equals("hkcu", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.Equals("hkcr", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.StartsWith("hkey", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // {{p|uid}} 这类"每用户数字 ID"占位：替换成 *，让用户自己确认
        foreach (Match m in UidTokenRegex().Matches(path))
        {
            var token = m.Groups["token"].Value;
            path = path.Replace(m.Value, "*");
        }

        // 展开路径首段令牌（只匹配路径开头，避免 "game" 误伤 "Games" 这类子串）
        var slash = path.IndexOfAny(SlashChars);
        var head = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? string.Empty : path[slash..];

        if (TokenMap.TryGetValue(head, out var mapped))
        {
            path = mapped + rest;
        }

        // 清理：反斜杠统一、去掉多余分隔符、去掉空段
        path = path.Replace('/', '\\');
        path = Regex.Replace(path, @"\\{2,}", "\\");
        path = path.Trim('\\', ' ', '|');

        if (path.Length == 0)
        {
            return null;
        }

        return path;
    }

    /// <summary>匹配模板内的 {{p|path}}。</summary>
    [GeneratedRegex(@"\{\{\s*p\s*\|(?<path>[^{}]+?)\s*\}\}")]
    private static partial Regex PathTokenRegex();

    /// <summary>匹配每用户 ID 令牌（{{p|uid}} / {{p|id}} / {{p|steamid}} 等）。</summary>
    [GeneratedRegex(@"\{\{\s*p\s*\|(?<token>uid|id|steamid|userid|username)\s*\}\}")]
    private static partial Regex UidTokenRegex();
}
