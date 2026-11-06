namespace GameSaveSync.Core.Theming;

/// <summary>预设主题色（Material Design 3 seed 的简化实现：手工调校的亮/暗色板）。</summary>
public enum ThemeSeed
{
    /// <summary>默认青（M4 以来的原版色）。</summary>
    Teal = 0,

    Indigo = 1,

    Emerald = 2,

    Amber = 3,

    Rose = 4,

    /// <summary>石墨：低饱和中性蓝，适合喜欢低调的用户。</summary>
    Slate = 5,
}

/// <summary>外观模式。</summary>
public enum ThemeMode
{
    /// <summary>跟随 Windows 系统（读注册表 AppsUseLightTheme）。</summary>
    System = 0,

    Light = 1,

    Dark = 2,
}

/// <summary>手工调校的色板：每个 seed 给亮/暗两套 P/S/T 四色组；中性色由亮暗决定。</summary>
public static class ThemePalettes
{
    private sealed record SeedColors(
        string LightPrimary, string LightPrimaryContainer,
        string LightSecondary, string LightSecondaryContainer,
        string LightTertiary, string LightTertiaryContainer,
        string DarkPrimary, string DarkPrimaryContainer,
        string DarkSecondary, string DarkSecondaryContainer,
        string DarkTertiary, string DarkTertiaryContainer);

    private static readonly IReadOnlyDictionary<ThemeSeed, SeedColors> Seeds = new Dictionary<ThemeSeed, SeedColors>
    {
        [ThemeSeed.Teal] = new(
            "#00696D", "#9CF1F5", "#4A6364", "#CCE8E9", "#4B607C", "#D3E4FF",
            "#4CD9E0", "#002022", "#B1CBCC", "#051F20", "#B4C8E8", "#041C35"),

        [ThemeSeed.Indigo] = new(
            "#3949AB", "#DDE1FF", "#575E71", "#DAE2F9", "#715B73", "#FBD7FC",
            "#BAC3FF", "#1F2570", "#BFC6DC", "#1B2B41", "#E0B9E1", "#3F2941"),

        [ThemeSeed.Emerald] = new(
            "#006E1C", "#9CF6AB", "#406841", "#C0ECC0", "#11611F", "#B8F1BA",
            "#7ADB84", "#002104", "#A7D2A6", "#0F200F", "#8CD89C", "#002E0B"),

        [ThemeSeed.Amber] = new(
            "#7E5700", "#FFDEA6", "#6F5B40", "#F8E0BB", "#77572C", "#FFDDB3",
            "#FFB949", "#261A00", "#DCC29C", "#241A0D", "#EFC284", "#271903"),

        [ThemeSeed.Rose] = new(
            "#A61E4D", "#FFD9E0", "#775659", "#FFD9DD", "#9C4057", "#FFD9DE",
            "#FFB1C0", "#5C1129", "#E5BDC0", "#2C1517", "#FFB1C1", "#3F001C"),

        [ThemeSeed.Slate] = new(
            "#454E5C", "#D9E2F4", "#5B5D64", "#E1E0EC", "#39556A", "#D0E4F7",
            "#B4C4DF", "#232F3F", "#C3C2CC", "#262A33", "#A2CBE9", "#0C2740"),
    };

    /// <summary>中性色（surface/outline/error 等）：亮暗各一套，不随 seed 变。</summary>
    private static readonly IReadOnlyDictionary<string, (string Light, string Dark)> Neutral = new Dictionary
        <string, (string, string)>
    {
        ["Surface"] = ("#F5FAF9", "#101415"),
        ["OnSurface"] = ("#171D1D", "#DDE3E2"),
        ["SurfaceVariant"] = ("#DAE4E4", "#3F4949"),
        ["OnSurfaceVariant"] = ("#3F4949", "#BEC8C7"),
        ["SurfaceDim"] = ("#D5DBDA", "#101415"),
        ["SurfaceBright"] = ("#F5FAF9", "#363B3C"),
        ["SurfaceContainerLowest"] = ("#FFFFFF", "#0B0F0F"),
        ["SurfaceContainerLow"] = ("#EFF5F4", "#191D1D"),
        ["SurfaceContainer"] = ("#E9EFEE", "#1D2122"),
        ["SurfaceContainerHigh"] = ("#E3E9E8", "#272B2C"),
        ["SurfaceContainerHighest"] = ("#DDE4E3", "#323636"),
        ["Outline"] = ("#6F7979", "#889291"),
        ["OutlineVariant"] = ("#BEC8C8", "#3F4949"),
        ["InverseSurface"] = ("#2B3232", "#DDE3E2"),
        ["InverseOnSurface"] = ("#ECF2F1", "#2B3232"),
        ["InversePrimary"] = ("#4CD9E0", "#00696D"),
        ["Scrim"] = ("#000000", "#000000"),
        ["Error"] = ("#BA1A1A", "#FFB4AB"),
        ["OnError"] = ("#FFFFFF", "#690005"),
        ["ErrorContainer"] = ("#FFDAD6", "#93000A"),
        ["OnErrorContainer"] = ("#410002", "#FFDAD6"),
        ["SuccessContainer"] = ("#B7F0C4", "#1F4D28"),
        ["OnSuccessContainer"] = ("#00210B", "#B7F0C4"),
        ["WarningContainer"] = ("#FFDF9E", "#5C4300"),
        ["OnWarningContainer"] = ("#261A00", "#FFDF9E"),
    };

    /// <summary>纯 CLR 颜色（Core 不引用 WPF）：A/R/G/B 分量。</summary>
    public sealed record PaletteColor(byte A, byte R, byte G, byte B);

    public static IReadOnlyList<KeyValuePair<string, PaletteColor>> For(ThemeSeed seed, bool dark)
    {
        var result = new List<KeyValuePair<string, PaletteColor>>(42);

        foreach (var (key, (light, _)) in Neutral)
        {
            result.Add(new(key, Parse(dark ? Neutral[key].Dark : light)));
        }

        // 未知 seed（如配置文件被手改）必须回退到默认色板：
        // 否则 P/S/T 共 12 个角色色全部缺失，装上去的主题会大面积丢色。
        if (!Seeds.TryGetValue(seed, out var colors))
        {
            colors = Seeds[ThemeSeed.Teal];
        }

        result.Add(new("Primary", Parse(dark ? colors.DarkPrimary : colors.LightPrimary)));
        result.Add(new("OnPrimary", Parse("#FFFFFF")));
        result.Add(new("PrimaryContainer", Parse(dark ? colors.DarkPrimaryContainer : colors.LightPrimaryContainer)));
        result.Add(new("OnPrimaryContainer", Parse(dark ? "#FFFFFF" : "#111111")));

        result.Add(new("Secondary", Parse(dark ? colors.DarkSecondary : colors.LightSecondary)));
        result.Add(new("OnSecondary", Parse("#FFFFFF")));
        result.Add(new("SecondaryContainer", Parse(dark ? colors.DarkSecondaryContainer : colors.LightSecondaryContainer)));
        result.Add(new("OnSecondaryContainer", Parse(dark ? "#FFFFFF" : "#111111")));

        result.Add(new("Tertiary", Parse(dark ? colors.DarkTertiary : colors.LightTertiary)));
        result.Add(new("OnTertiary", Parse("#FFFFFF")));
        result.Add(new("TertiaryContainer", Parse(dark ? colors.DarkTertiaryContainer : colors.LightTertiaryContainer)));
        result.Add(new("OnTertiaryContainer", Parse(dark ? "#FFFFFF" : "#111111")));

        return result;
    }

    private static PaletteColor Parse(string hex)
    {
        var hexColor = hex.TrimStart('#');
        if (hexColor.Length == 6)
        {
            hexColor = "FF" + hexColor;
        }

        var value = Convert.ToUInt32(hexColor, 16);
        return new PaletteColor(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }
}
