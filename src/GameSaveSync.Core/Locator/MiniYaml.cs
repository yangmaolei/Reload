using System.Text;

namespace GameSaveSync.Core.Locator;

/// <summary>
/// 只覆盖规则库需要的 YAML 子集：<c>key: value</c>、<c>key:</c> + 缩进块、<c>- item</c>、
/// <c>- key: value</c>（后续行同缩进继续同一条目）。带引号的值处理 <c>\\ \" \n \t</c> 转义，
/// 不带引号的值按字面量处理（Windows 路径可直接写单反斜杠）。
/// </summary>
public static class MiniYaml
{
    private sealed record Line(int Indent, string Text);

    public static object? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return null;
        }

        var index = 0;
        return ParseBlock(lines, ref index, lines[0].Indent);
    }

    public static IReadOnlyDictionary<string, object?>? ParseMapping(string text) =>
        Parse(text) as IReadOnlyDictionary<string, object?>;

    private static object? ParseBlock(List<Line> lines, ref int index, int indent)
    {
        if (index >= lines.Count)
        {
            return null;
        }

        return IsSequenceItem(lines[index].Text)
            ? ParseSequence(lines, ref index, indent)
            : ParseMap(lines, ref index, indent);
    }

    private static List<object?> ParseSequence(List<Line> lines, ref int index, int indent)
    {
        var list = new List<object?>();

        while (index < lines.Count && lines[index].Indent == indent && IsSequenceItem(lines[index].Text))
        {
            var text = lines[index].Text;
            var rest = text.Length > 1 ? text[2..].Trim() : string.Empty;
            index++;

            if (rest.Length == 0)
            {
                list.Add(index < lines.Count && lines[index].Indent > indent
                    ? ParseBlock(lines, ref index, lines[index].Indent)
                    : null);
                continue;
            }

            if (TrySplitKey(rest, out var key, out var value))
            {
                var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (value.Length > 0)
                {
                    map[key] = Scalar(value);
                }
                else if (index < lines.Count && lines[index].Indent > indent && !IsSequenceItem(lines[index].Text))
                {
                    map[key] = ParseBlock(lines, ref index, lines[index].Indent);
                }
                else
                {
                    map[key] = null;
                }

                // 同一个条目后续的键（比 "- " 多缩进一级）
                if (index < lines.Count && lines[index].Indent > indent && !IsSequenceItem(lines[index].Text))
                {
                    ParseMapEntries(lines, ref index, lines[index].Indent, map);
                }

                list.Add(map);
                continue;
            }

            list.Add(Scalar(rest));
        }

        return list;
    }

    private static Dictionary<string, object?> ParseMap(List<Line> lines, ref int index, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ParseMapEntries(lines, ref index, indent, map);
        return map;
    }

    private static void ParseMapEntries(
        List<Line> lines,
        ref int index,
        int indent,
        Dictionary<string, object?> map)
    {
        while (index < lines.Count && lines[index].Indent == indent && !IsSequenceItem(lines[index].Text))
        {
            var text = lines[index].Text;
            if (!TrySplitKey(text, out var key, out var value))
            {
                break;
            }

            index++;
            if (value.Length > 0)
            {
                map[key] = Scalar(value);
                continue;
            }

            if (index < lines.Count && lines[index].Indent > indent)
            {
                map[key] = ParseBlock(lines, ref index, lines[index].Indent);
            }
            else
            {
                map[key] = null;
            }
        }
    }

    private static bool IsSequenceItem(string text) =>
        text.StartsWith("- ", StringComparison.Ordinal) || text == "-";

    private static bool TrySplitKey(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var separator = -1;
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (text[i] == ':' &&
                (i + 1 == text.Length || text[i + 1] == ' ' || IsTightKeyValueSeparator(text, i)))
            {
                separator = i;
                break;
            }
        }

        if (separator <= 0)
        {
            return false;
        }

        key = text[..separator].Trim();
        value = text[(separator + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>
    /// 判断"冒号后无空格"是否仍应视作 key/value 分隔。
    /// YAML 规范要求冒号后有空格，但真实世界的 yaml 常不守规矩
    /// （如 <c>namePattern:Old*</c>）。这类写法若不认，整块 map 会被丢弃，
    /// 用户自定义的整条规则静默失效。
    /// 但 <c>http://host/path</c> 这类值里的冒号不能当分隔符 → 排除 '/'。
    /// </summary>
    private static bool IsTightKeyValueSeparator(string text, int colonIndex)
    {
        var next = colonIndex + 1;
        if (next >= text.Length)
        {
            return false;
        }

        if (text[next] == '/')
        {
            return false;
        }

        // 键必须是标识符形态（字母/数字/下划线/连字符/点），否则多半是值里的符号
        for (var i = 0; i < colonIndex; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return colonIndex > 0;
    }

    public static string? Scalar(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            var inner = trimmed[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i + 1 >= inner.Length)
                {
                    builder.Append(inner[i]);
                    continue;
                }

                var escaped = inner[++i];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => escaped,
                });
            }

            return builder.ToString();
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static List<Line> SplitLines(string text)
    {
        var result = new List<Line>();

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Replace("\t", "  ", StringComparison.Ordinal);
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            var content = line[indent..].TrimEnd();
            if (content.Length == 0 || content.StartsWith('#'))
            {
                continue;
            }

            content = StripComment(content);
            if (content.Length == 0)
            {
                continue;
            }

            result.Add(new Line(indent, content));
        }

        return result;
    }

    private static string StripComment(string content)
    {
        var inQuotes = false;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (content[i] == '#' && (i == 0 || char.IsWhiteSpace(content[i - 1])))
            {
                return content[..i].TrimEnd();
            }
        }

        return content;
    }
}
