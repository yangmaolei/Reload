using System.Globalization;
using System.Windows.Data;
using GameSaveSync.Core.Covers;

namespace GameSaveSync.App.Converters;

/// <summary>
/// 分页指示器的选中判定：比较当前项与当前选中的海报形态。
/// 用法：<c>MultiBinding</c> 传入 <c>[当前项形态, SelectedPosterKind]</c>，返回 bool。
/// </summary>
public sealed class PosterIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return false;
        }

        if (values[0] is not PosterKind item || values[1] is not PosterKind current)
        {
            return false;
        }

        return item == current;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("指示器状态是单向换算，不能反推。");
}
