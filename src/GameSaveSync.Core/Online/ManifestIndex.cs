using System.Text;

namespace GameSaveSync.Core.Online;

/// <summary>清单里的一个游戏块（顶层键 + 其内容行范围）。</summary>
public sealed class LudusaviManifestBlock
{
    public LudusaviManifestBlock(string name, int startLine, int endLine)
    {
        Name = name;
        StartLine = startLine;
        EndLine = endLine;
    }

    /// <summary>游戏名（清单里的顶层键）。</summary>
    public string Name { get; }

    /// <summary>块内容起始行（含）。</summary>
    public int StartLine { get; }

    /// <summary>块内容结束行（不含）。</summary>
    public int EndLine { get; }
}

/// <summary>
/// 清单索引：游戏名 → 块位置，以及 Steam AppID → 块位置。
/// <para>
/// 用行级扫描而不是完整 YAML DOM：清单有 5 万多个条目、上万个子节点，
/// 完整解析会吃掉几百 MB 内存且要好几秒；行级扫描只记录"哪一行是哪个游戏的块"，
/// 内存几 MB、耗时不到一秒，查询时再只解析命中块（几 KB）。
/// </para>
/// </summary>
public sealed class ManifestIndex
{
    /// <summary>前缀匹配时，较短一方至少要有这么多字符。</summary>
    private const int MinPartialMatchLength = 4;

    private readonly string[] _lines;
    private readonly Dictionary<int, int> _bySteamId;
    private readonly Dictionary<string, int> _byName;

    private ManifestIndex(string[] lines, Dictionary<int, int> bySteamId, Dictionary<string, int> byName)
    {
        _lines = lines;
        _bySteamId = bySteamId;
        _byName = byName;
    }

    /// <summary>全部块（顺序与清单一致）。</summary>
    public IReadOnlyList<LudusaviManifestBlock> Blocks { get; private init; } = Array.Empty<LudusaviManifestBlock>();

    /// <summary>清单位于某个块内的文本（不含顶层键行）。</summary>
    public string GetBlockText(LudusaviManifestBlock block)
    {
        var end = Math.Min(block.EndLine, _lines.Length);
        if (block.StartLine >= end)
        {
            return string.Empty;
        }

        return string.Join("\n", _lines, block.StartLine, end - block.StartLine);
    }

    /// <summary>
    /// 找出与给定游戏对应的块。
    /// 优先用 Steam AppID 精确命中（最可靠，能避开同名游戏）；没有 AppID 或未命中时按名字匹配。
    /// </summary>
    public LudusaviManifestBlock? FindBlock(string gameName, string? externalId)
    {
        if (!string.IsNullOrWhiteSpace(externalId) &&
            int.TryParse(externalId, out var appId) &&
            _bySteamId.TryGetValue(appId, out var byId))
        {
            return Blocks[byId];
        }

        var key = NormalizeName(gameName);
        if (key.Length == 0)
        {
            return null;
        }

        if (_byName.TryGetValue(key, out var exact))
        {
            return Blocks[exact];
        }

        // 前缀匹配：较短的一方至少 4 个字符。
        // 【为什么要这么严】清单里有一堆单字母/双字母的游戏名（"C"、"D"、"E"…），
        // 规范化后就是一个字符。如果用"任意包含"来匹配，
        // 查 "Lossless Scaling" 会被 "c" 命中（因为名字里带 c）——完全不相干。
        // 限定成"前缀关系"且短名不少于 4 个字符，才不会乱命中。
        if (key.Length < MinPartialMatchLength)
        {
            return null;
        }

        LudusaviManifestBlock? best = null;
        foreach (var pair in _byName)
        {
            var candidateKey = pair.Key;
            if (candidateKey.Length < MinPartialMatchLength)
            {
                continue;
            }

            var isPrefix = candidateKey.StartsWith(key, StringComparison.Ordinal) ||
                           key.StartsWith(candidateKey, StringComparison.Ordinal);
            if (!isPrefix)
            {
                continue;
            }

            var candidate = Blocks[pair.Value];
            if (best is null || candidate.Name.Length < best.Name.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>把游戏名归一化成可比较的键：去掉空白与标点，只留字母数字并转小写。</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>扫描清单文本，建立索引。</summary>
    public static ManifestIndex Build(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<LudusaviManifestBlock>();
        var bySteamId = new Dictionary<int, int>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);

        string? currentName = null;
        var currentStart = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // 顶层键：行首无空白，且是 "key:" 或 "key: value" 或 "key":"
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#')
            {
                continue;
            }

            if (!TryReadTopLevelKey(line, out var key, out var hasInlineValue))
            {
                continue;
            }

            if (currentName is not null)
            {
                blocks.Add(new LudusaviManifestBlock(currentName, currentStart, i));
                RegisterSteamId(lines, currentStart, i, blocks.Count - 1, bySteamId);
                RegisterName(currentName, blocks.Count - 1, byName);
            }

            if (hasInlineValue)
            {
                // "key": value 这种没有子块，直接跳过
                currentName = null;
                currentStart = i + 1;
                continue;
            }

            currentName = key;
            currentStart = i + 1;
        }

        if (currentName is not null)
        {
            blocks.Add(new LudusaviManifestBlock(currentName, currentStart, lines.Length));
            RegisterSteamId(lines, currentStart, lines.Length, blocks.Count - 1, bySteamId);
            RegisterName(currentName, blocks.Count - 1, byName);
        }

        return new ManifestIndex(lines, bySteamId, byName)
        {
            Blocks = blocks,
        };
    }

    /// <summary>
    /// 读顶层键。清单里的键可能带引号（名字里有特殊字符时）也可能不带。
    /// </summary>
    private static bool TryReadTopLevelKey(string line, out string key, out bool hasInlineValue)
    {
        key = string.Empty;
        hasInlineValue = false;

        string rest;
        if (line[0] == '"')
        {
            var close = FindClosingQuote(line);
            if (close < 0)
            {
                return false;
            }

            key = Unescape(line[1..close]);
            rest = line[(close + 1)..].TrimStart();
            if (!rest.StartsWith(':'))
            {
                return false;
            }

            rest = rest[1..].Trim();
        }
        else
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            key = line[..colon].Trim();
            rest = line[(colon + 1)..].Trim();
        }

        if (key.Length == 0)
        {
            return false;
        }

        hasInlineValue = rest.Length > 0;
        return true;
    }

    private static int FindClosingQuote(string line)
    {
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>在块内找 <c>steam:</c> 段下的 <c>id:</c>。</summary>
    private static void RegisterSteamId(
        string[] lines,
        int start,
        int end,
        int blockIndex,
        Dictionary<int, int> bySteamId)
    {
        var inSteam = false;
        for (var i = start; i < end && i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var indent = CountIndent(line);
            var trimmed = line.Trim();

            if (indent == 2 && trimmed.Equals("steam:", StringComparison.Ordinal))
            {
                inSteam = true;
                continue;
            }

            if (indent <= 2 && trimmed.Length > 0)
            {
                // 离开 steam 段
                inSteam = false;
            }

            if (inSteam && indent == 4 && trimmed.StartsWith("id:", StringComparison.Ordinal))
            {
                var value = trimmed[3..].Trim();
                if (int.TryParse(value, out var appId) && appId > 0)
                {
                    bySteamId.TryAdd(appId, blockIndex);
                }

                break;
            }
        }
    }

    private static void RegisterName(string name, int blockIndex, Dictionary<string, int> byName)
    {
        var key = NormalizeName(name);
        if (key.Length == 0)
        {
            return;
        }

        byName.TryAdd(key, blockIndex);
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
