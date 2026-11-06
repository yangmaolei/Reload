namespace GameSaveSync.Infrastructure;

/// <summary>
/// 长路径工具。Windows 上 &gt; 260 字符的路径必须走 <c>\\?\</c> 前缀，
/// 且该前缀会关闭路径规范化（<c>.</c> / <c>..</c> / 尾部空格都要先自行规范化）。
/// </summary>
public static class LongPath
{
    private const string LocalPrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>给绝对路径加载 &lt;c&gt;\\?\&lt;/c&gt; 前缀；已带前缀或相对路径按需处理。</summary>
    public static string Ensure(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(LocalPrefix, StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC：\\server\share\x → \\?\UNC\server\share\x
            return UncPrefix + full[2..];
        }

        return LocalPrefix + full;
    }

    /// <summary>去掉 <c>\\?\</c> / <c>\\?\UNC\</c> 前缀，得到用户可见路径。</summary>
    public static string Strip(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[UncPrefix.Length..];
        }

        if (path.StartsWith(LocalPrefix, StringComparison.Ordinal))
        {
            return path[LocalPrefix.Length..];
        }

        return path;
    }

    public static bool HasPrefix(string path) =>
        !string.IsNullOrEmpty(path) &&
        (path.StartsWith(LocalPrefix, StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal));
}
