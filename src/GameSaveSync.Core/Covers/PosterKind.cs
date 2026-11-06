namespace GameSaveSync.Core.Covers;

/// <summary>
/// 海报形态。同一款游戏在不同平台有两套主视觉，尺寸比例差异很大：
/// <list type="bullet">
/// <item><see cref="Portrait"/>：竖屏长图（Steam <c>library_600x900.jpg</c>，2:3），适合大尺寸主视觉。</item>
/// <item><see cref="Square"/>：方形头图（Steam <c>header.jpg</c>），宽扁，适合卡片位。</item>
/// </list>
/// </summary>
public enum PosterKind
{
    /// <summary>竖屏长图（大尺寸主视觉）。</summary>
    Portrait = 0,

    /// <summary>方形头图（卡片位）。</summary>
    Square = 1,
}

public static class PosterKinds
{
    public static readonly IReadOnlyList<PosterKind> All = new[] { PosterKind.Portrait, PosterKind.Square };

    /// <summary>形态的稳定标识，用作缓存文件名的一部分（不含扩展名）。</summary>
    public static string Slug(this PosterKind kind) => kind switch
    {
        PosterKind.Portrait => "p",
        PosterKind.Square => "s",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>界面展示名。</summary>
    public static string DisplayName(this PosterKind kind) => kind switch
    {
        PosterKind.Portrait => "竖屏长图",
        PosterKind.Square => "方形头图",
        _ => kind.ToString(),
    };

    public static PosterKind Parse(string value) => value switch
    {
        "p" or "portrait" => PosterKind.Portrait,
        "s" or "square" => PosterKind.Square,
        _ => throw new ArgumentException($"未知的海报形态：{value}", nameof(value)),
    };

    public static bool TryParse(string? value, out PosterKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = PosterKind.Portrait;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "p":
            case "portrait":
                kind = PosterKind.Portrait;
                return true;
            case "s":
            case "square":
                kind = PosterKind.Square;
                return true;
            default:
                kind = PosterKind.Portrait;
                return false;
        }
    }
}
