using System.Text;

namespace GameSaveSync.Core.Locator;

/// <summary>Valve KeyValues 节点。<c>libraryfolders.vdf</c> / <c>appmanifest_*.acf</c> 都用这个格式。</summary>
public sealed class VdfNode
{
    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    /// <summary>叶子节点的值；有子节点时为 null。</summary>
    public string? Value { get; }

    public List<VdfNode> Children { get; } = new();

    public VdfNode? Child(string key) =>
        Children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<VdfNode> ChildrenOf(string key) =>
        Children.Where(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string? ChildValue(string key) => Child(key)?.Value;

    public override string ToString() => Value is null ? $"{Key} ({Children.Count})" : $"{Key} = {Value}";
}

/// <summary>
/// 只实现 Steam 用到的 KeyValues 子集：带引号/不带引号的键值、嵌套 <c>{ }</c>、<c>//</c> 注释、
/// 反斜杠转义。条件标记（<c>[$WIN32]</c>）会被当作普通键名丢弃，不影响我们要的字段。
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = Tokenize(text);
        var root = new VdfNode("<root>");
        ParseInto(root.Children, tokens, 0);
        return root;
    }

    /// <summary>解析并返回第一个顶层节点（VDF 文件通常只有一个）。</summary>
    public static VdfNode? ParseSingle(string text) => Parse(text).Children.FirstOrDefault();

    private static int ParseInto(List<VdfNode> target, List<string> tokens, int index)
    {
        while (index < tokens.Count)
        {
            var key = tokens[index++];
            if (key == "}")
            {
                return index;
            }

            if (index >= tokens.Count)
            {
                target.Add(new VdfNode(key));
                return index;
            }

            var next = tokens[index];
            if (next == "{")
            {
                var node = new VdfNode(key);
                index = ParseInto(node.Children, tokens, index + 1);
                target.Add(node);
                continue;
            }

            if (next == "}")
            {
                target.Add(new VdfNode(key));
                return index;
            }

            target.Add(new VdfNode(key, next));
            index++;
        }

        return index;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                var builder = new StringBuilder();
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == '\\' && i + 1 < text.Length)
                    {
                        var escaped = text[i + 1];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => escaped,
                        });
                        i += 2;
                        continue;
                    }

                    if (ch == '"')
                    {
                        i++;
                        break;
                    }

                    builder.Append(ch);
                    i++;
                }

                tokens.Add(builder.ToString());
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }
}
