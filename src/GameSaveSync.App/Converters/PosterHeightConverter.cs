using System.Globalization;
using System.Windows.Data;
using GameSaveSync.Core.Covers;

namespace GameSaveSync.App.Converters;

/// <summary>
/// 海报容器高度换算：<c>高度 = 宽度 × 形态宽高比</c>。
/// 竖屏长图是 2:3（比值 1.5），方形头图按 1:1 处理。
/// 配合 <c>Stretch="Uniform"</c>，图片等比缩放，既不变形也不溢出容器。
/// </summary>
public sealed class PosterHeightConverter : IMultiValueConverter
{
    /// <summary>竖屏长图的高宽比（300×450）。</summary>
    public const double PortraitAspect = 1.5;

    /// <summary>方形头图的高宽比。</summary>
    public const double SquareAspect = 1.0;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return double.NaN;
        }

        if (!TryToDouble(values[0], out var width))
        {
            return double.NaN;
        }

        var aspect = values[1] is PosterKind kind ? AspectOf(kind) : SquareAspect;
        return width * aspect;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("海报高度是单向换算，不能反推。");

    /// <summary>取形态的宽高比。</summary>
    public static double AspectOf(PosterKind kind) => kind switch
    {
        PosterKind.Portrait => PortraitAspect,
        PosterKind.Square => SquareAspect,
        _ => SquareAspect,
    };

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return !double.IsNaN(d) && !double.IsInfinity(d);
            case int i:
                result = i;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
