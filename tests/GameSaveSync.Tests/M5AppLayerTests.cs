using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GameSaveSync.App.Converters;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Paths;
using GameSaveSync.Core.Theming;

namespace GameSaveSync.Tests;

/// <summary>
/// App 层（UI 逻辑）测试：转换器、路径、主题色板。
/// 这些全是纯函数/纯数据，不依赖真实窗口，可在单测里直接跑。
/// </summary>
public class M5AppLayerTests
{
    // ============================================================
    // 转换器
    // ============================================================

    [Theory]
    [InlineData(true, "invert", Visibility.Collapsed)]
    [InlineData(false, "invert", Visibility.Visible)]
    [InlineData(true, "INVERT", Visibility.Collapsed)]
    [InlineData(true, null, Visibility.Visible)]
    [InlineData(null, null, Visibility.Collapsed)]
    public void BoolToVisibility_HandlesInvertAndNull(bool? value, string? parameter, Visibility expected)
    {
        var converter = new BoolToVisibilityConverter();

        var result = converter.Convert(value, typeof(Visibility), parameter, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BoolToVisibility_ConvertBack_IgnoresInvert()
    {
        var converter = new BoolToVisibilityConverter();

        Assert.True((bool)converter.ConvertBack(Visibility.Visible, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.False((bool)converter.ConvertBack(Visibility.Collapsed, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0, null, Visibility.Visible)]
    [InlineData(5, null, Visibility.Collapsed)]
    [InlineData(0, "invert", Visibility.Collapsed)]
    [InlineData(5, "invert", Visibility.Visible)]
    [InlineData(-1, null, Visibility.Collapsed)]
    public void CountToVisibility_HandlesZeroAndInvert(int count, string? parameter, Visibility expected)
    {
        var converter = new CountToVisibilityConverter();

        Assert.Equal(expected, converter.Convert(count, typeof(Visibility), parameter, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CountToVisibility_ConvertBack_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            new CountToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(int), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToVisibility_EmptyStringIsStillVisible()
    {
        var converter = new NullToVisibilityConverter();

        Assert.Equal(
            Visibility.Visible,
            converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            Visibility.Visible,
            converter.Convert(null, typeof(Visibility), "invert", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("abc", Visibility.Visible)]
    [InlineData("", Visibility.Collapsed)]
    [InlineData("   ", Visibility.Collapsed)]
    [InlineData(null, Visibility.Collapsed)]
    public void TextToVisibility_HandlesWhitespace(string? value, Visibility expected)
    {
        Assert.Equal(
            expected,
            new TextToVisibilityConverter().Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("settings", "settings", Visibility.Visible)]
    [InlineData("SETTINGS", "settings", Visibility.Visible)]
    [InlineData("games", "settings", Visibility.Collapsed)]
    [InlineData(null, "settings", Visibility.Collapsed)]
    public void PageToVisibility_MatchesCaseInsensitively(string? value, string? parameter, Visibility expected)
    {
        Assert.Equal(
            expected,
            new PageToVisibilityConverter().Convert(value, typeof(Visibility), parameter, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PageToVisibility_NullValueAndNullParameter_Collapsed()
    {
        // 两边都是空串时若判等，会错误地显示页面
        Assert.Equal(
            Visibility.Collapsed,
            new PageToVisibilityConverter().Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Hollow Knight", "HK")]
    [InlineData("Dark Souls III", "DS")]
    [InlineData("Halo: CE", "HC")]
    [InlineData("A", "A")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    [InlineData("   ", "?")]
    public void Initials_UsesFirstLettersOrFallback(string? name, string expected)
    {
        Assert.Equal(
            expected,
            new InitialsConverter().Convert(name, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Initials_ChineseName_TakesFirstCharacter()
    {
        Assert.Equal(
            "中",
            new InitialsConverter().Convert("中文游戏", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NameToBrush_IsStableForSameName()
    {
        var converter = new NameToBrushConverter();

        var first = (SolidColorBrush)converter.Convert("Hollow Knight", typeof(Brush), null, CultureInfo.InvariantCulture);
        var second = (SolidColorBrush)converter.Convert("Hollow Knight", typeof(Brush), null, CultureInfo.InvariantCulture);

        Assert.Equal(first.Color, second.Color);
    }

    [Fact]
    public void NameToBrush_IntMinValueHash_DoesNotOverflow()
    {
        // "Gycxh!" 的 31 进制哈希恰为 int.MinValue：
        // Math.Abs(int.MinValue) 会抛 OverflowException → 游戏卡片渲染直接崩。
        var converter = new NameToBrushConverter();

        var brush = (SolidColorBrush)converter.Convert("Gycxh!", typeof(Brush), null, CultureInfo.InvariantCulture);

        Assert.NotNull(brush);
    }

    [Fact]
    public void NameToBrush_NullValue_DoesNotThrow()
    {
        var converter = new NameToBrushConverter();

        Assert.NotNull(converter.Convert(null, typeof(Brush), null, CultureInfo.InvariantCulture));
        Assert.NotNull(converter.Convert(string.Empty, typeof(Brush), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    public void BytesConverter_FormatsKnownMagnitudes(long bytes, string expected)
    {
        Assert.Equal(
            expected,
            new BytesConverter().Convert(bytes, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BytesConverter_NullAndInt_Work()
    {
        var converter = new BytesConverter();

        Assert.Equal("0 B", converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("1 KB", converter.Convert(1024, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void HexToBrush_InvalidHex_FallsBackToTransparent()
    {
        var converter = new HexToBrushConverter();

        Assert.Equal(Brushes.Transparent, converter.Convert("not-a-color", typeof(Brush), null, CultureInfo.InvariantCulture));
        Assert.Equal(Brushes.Transparent, converter.Convert(null, typeof(Brush), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void HexToBrush_ValidHex_ReturnsColor()
    {
        var converter = new HexToBrushConverter();

        var brush = (SolidColorBrush)converter.Convert("#FF0000", typeof(Brush), null, CultureInfo.InvariantCulture);

        Assert.Equal(Colors.Red, brush.Color);
    }

    [Fact]
    public void StateToBrush_KnownStates_ReturnDistinctColors()
    {
        var converter = new StateToBrushConverter();

        var synced = (SolidColorBrush)converter.Convert(
            GameSaveSync.App.ViewModels.GameSyncState.Synced, typeof(Brush), null, CultureInfo.InvariantCulture);
        var conflict = (SolidColorBrush)converter.Convert(
            GameSaveSync.App.ViewModels.GameSyncState.Conflict, typeof(Brush), null, CultureInfo.InvariantCulture);

        Assert.NotEqual(synced.Color, conflict.Color);
    }

    [Fact]
    public void TriggerGlyph_UnknownTrigger_FallsBackToManual()
    {
        var converter = new TriggerGlyphConverter();

        Assert.Equal(
            "\uE74E",
            converter.Convert((GameSaveSync.Core.Models.SnapshotTrigger)99, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            "\uE74E",
            converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OneWayConverters_ConvertBack_Throws()
    {
        var culture = CultureInfo.InvariantCulture;

        Assert.Throws<NotSupportedException>(() =>
            new CountToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(int), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new NullToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(object), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new TextToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(string), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new PageToVisibilityConverter().ConvertBack(Visibility.Visible, typeof(string), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new NameToBrushConverter().ConvertBack(null, typeof(object), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new DiffKindToBrushConverter().ConvertBack(null, typeof(object), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new LogLevelToBrushConverter().ConvertBack(null, typeof(object), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new TriggerGlyphConverter().ConvertBack(null, typeof(string), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new StateToBrushConverter().ConvertBack(null, typeof(object), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new BytesConverter().ConvertBack(null, typeof(long), null, culture));
        Assert.Throws<NotSupportedException>(() =>
            new HexToBrushConverter().ConvertBack(null, typeof(object), null, culture));
    }

    // ============================================================
    // AppPaths
    // ============================================================

    [Fact]
    public void AppPaths_RejectsBlankDataRoot()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(""));
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths("   "));
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(null!));
    }

    [Fact]
    public void AppPaths_BucketDirectory_RequiresTwoCharHash()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gss-paths-test"));

        Assert.Throws<ArgumentException>(() => paths.BucketDirectory("a"));
        Assert.Throws<ArgumentException>(() => paths.BucketDirectory(""));
        Assert.EndsWith("ab", paths.BucketDirectory("abcdef"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppPaths_ObjectPath_IsLowercased()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "gss-paths-test"));

        var path = paths.ObjectPath("ABCDEF");

        Assert.Contains("abcdef", path, StringComparison.Ordinal);
    }

    [Fact]
    public void AppPaths_ResolveDefault_UsesReloadDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "gss-resolve-" + Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "gss-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(localAppData);

        try
        {
            var paths = AppPaths.ResolveDefault(baseDir, localAppData);

            Assert.False(paths.IsPortable);
            Assert.Contains("Reload", paths.DataRoot, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void AppPaths_ResolveDefault_PortableFlag_UsesProgramDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "gss-portable-" + Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(Path.GetTempPath(), "gss-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(localAppData);
        File.WriteAllText(Path.Combine(baseDir, AppPaths.PortableFlagName), string.Empty);

        try
        {
            var paths = AppPaths.ResolveDefault(baseDir, localAppData);

            Assert.True(paths.IsPortable);
            Assert.Equal(Path.Combine(baseDir, "data"), paths.DataRoot, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    public void AppPaths_ResolveDefault_RejectsBlankBaseDirectory()
    {
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolveDefault("", "C:\\x"));
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.ResolveDefault(null!, "C:\\x"));
    }

    [Fact]
    public void AppPaths_EnsureCreated_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "gss-ensure-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);

        try
        {
            paths.EnsureCreated();
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.ObjectsDir));
            Assert.True(Directory.Exists(paths.ObjectsTempDir));
            Assert.True(Directory.Exists(paths.ManifestsDir));
            Assert.True(Directory.Exists(paths.LogsDir));
            Assert.True(Directory.Exists(paths.CoversDir));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ============================================================
    // 主题色板
    // ============================================================

    [Theory]
    [InlineData(ThemeSeed.Teal)]
    [InlineData(ThemeSeed.Indigo)]
    [InlineData(ThemeSeed.Emerald)]
    [InlineData(ThemeSeed.Amber)]
    [InlineData(ThemeSeed.Rose)]
    [InlineData(ThemeSeed.Slate)]
    public void ThemePalettes_EverySeedHasCompleteRoleSet(ThemeSeed seed)
    {
        foreach (var dark in new[] { false, true })
        {
            var palette = ThemePalettes.For(seed, dark);

            Assert.Equal(37, palette.Count);
            Assert.All(palette, p => Assert.Equal(0xFF, p.Value.A));
        }
    }

    [Fact]
    public void ThemePalettes_UnknownSeed_FallsBackToCompletePalette()
    {
        // 未知 seed 若静默返回残缺色板，装上去的主题会大面积丢色
        var palette = ThemePalettes.For((ThemeSeed)99, false);

        Assert.Equal(37, palette.Count);
    }

    [Fact]
    public void ThemePalettes_LightAndDarkDiffer()
    {
        var light = ThemePalettes.For(ThemeSeed.Teal, false).ToDictionary(p => p.Key, p => p.Value);
        var dark = ThemePalettes.For(ThemeSeed.Teal, true).ToDictionary(p => p.Key, p => p.Value);

        Assert.NotEqual(light["Primary"], dark["Primary"]);
        Assert.NotEqual(light["Surface"], dark["Surface"]);
    }

    // ============================================================
    // Glob 边界
    // ============================================================

    [Theory]
    [InlineData("?", "a", true)]
    [InlineData("?", "ab", false)]
    [InlineData("?", "", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    public void GlobMatcher_SingleCharWildcard(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatchPattern(pattern, value));
    }

    [Fact]
    public void GlobMatcher_ExcludeWinsOverInclude()
    {
        var matcher = new GlobMatcher(new[] { "*.sav" }, new[] { "skip*" });

        Assert.True(matcher.IsMatch("keep.sav"));
        Assert.False(matcher.IsMatch("skip.sav"));
    }

    [Fact]
    public void GlobMatcher_EmptyIncludeMatchesEverything()
    {
        var matcher = new GlobMatcher(Array.Empty<string>(), Array.Empty<string>());

        Assert.True(matcher.IsMatch("anything.bin"));
        Assert.True(matcher.IsMatch(@"deep\nested\file.bin"));
    }

    [Fact]
    public void GlobMatcher_Normalize_ConvertsSlashToBackslash()
    {
        Assert.Equal(@"a\b\c", GlobMatcher.Normalize("a/b/c"));
    }

    [Fact]
    public void GlobMatcher_BlankPattern_MatchesEverything()
    {
        Assert.True(GlobMatcher.IsMatchPattern("   ", "anything"));
    }

    // ============================================================
    // 远端根目录默认值：应与品牌名一致
    // ============================================================

    [Fact]
    public void DefaultRemoteRoot_MatchesAppName()
    {
        Assert.Equal(AppPaths.AppName, AppPaths.DefaultRemoteRoot);

        var options = new GameSaveSync.WebDav.WebDavOptions();
        Assert.Equal(AppPaths.DefaultRemoteRoot, options.RemoteRoot);
    }

    [Fact]
    public void AppPaths_LegacyName_IsPreservedForMigration()
    {
        // 老目录名必须保留：启动时靠它把数据从 GameSaveSync 迁到 Reload
        Assert.Equal("GameSaveSync", AppPaths.LegacyAppName);
        Assert.NotEqual(AppPaths.AppName, AppPaths.LegacyAppName);
    }
}
