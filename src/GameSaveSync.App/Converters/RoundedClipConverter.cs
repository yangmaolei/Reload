using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GameSaveSync.App.Converters;

/// <summary>
/// 由"目标尺寸 + 圆角半径"生成圆角矩形裁剪几何。
/// </summary>
/// <remarks>
/// WPF 的 <c>Border.CornerRadius</c> <b>不会裁剪子内容</b>（实测：即使
/// <c>ClipToBounds="True"</c>，角落像素仍是子内容的原色），所以图片要变成圆角
/// 必须自己提供 <c>Image.Clip</c>。
///
/// 这里把矩形尺寸和圆角都做成可绑定的，避免在 XAML 里硬编码坐标 —— 改一次
/// 卡片尺寸就要同步改几处裁剪矩形，很容易漏。
///
/// 参数：<c>width;height;radius</c>（分号分隔），也支持单值（正方形 + 同半径）。
/// </remarks>
public sealed class RoundedClipConverter : IMultiValueConverter
{
    public object? Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 3)
        {
            return null;
        }

        if (!TryDouble(values[0], out var width) ||
            !TryDouble(values[1], out var height) ||
            !TryDouble(values[2], out var radius))
        {
            return null;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // 圆角不能超过边长的一半，否则 WPF 会自己钳制，两边不一致会出现"半圆化"
        var limit = Math.Min(width, height) / 2;
        if (radius > limit)
        {
            radius = limit;
        }

        if (radius < 0)
        {
            radius = 0;
        }

        return new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
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
