using System.Globalization;
using System.Windows.Data;

namespace GameSaveSync.App.Converters;

/// <summary>
/// int ↔ bool 双向转换，专供 RadioButton 绑定整型索引。
/// <para>
/// RadioButton 的 IsChecked 是 bool，无法直接绑定 int。
/// 参数是"目标整数值"：绑定的 int 等于参数时选中。
/// ConverterParameter 必须是 int（XAML 里写 "1" 会被解析成字符串，
/// 所以这里同时兼容字符串数字）。
/// </para>
/// </summary>
public sealed class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value is int number ? number : int.TryParse(value?.ToString(), out var parsed) ? parsed : -1;
        var target = ParseParameter(parameter);

        return target.HasValue && current == target.Value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            // 取消勾选时不能把索引改成 0 —— 那会误触发"关闭自启动"。
            // 返回绑定源原值：Binding.DoNothing 表示不写回。
            return System.Windows.Data.Binding.DoNothing;
        }

        var target = ParseParameter(parameter);
        return target ?? 0;
    }

    private static int? ParseParameter(object? parameter)
    {
        if (parameter is int number)
        {
            return number;
        }

        return int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
