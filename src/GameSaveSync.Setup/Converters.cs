using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GameSaveSync.Setup;

/// <summary>
/// 把 <see cref="SetupStep"/> 转成 Visibility：
/// 参数为步骤名时显示该步骤，参数为 "invert" 时取反。
/// </summary>
public sealed class SetupStepToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SetupStep step)
        {
            return Visibility.Collapsed;
        }

        var param = parameter as string;

        if (string.Equals(param, "invert", StringComparison.OrdinalIgnoreCase))
        {
            return Visibility.Collapsed;
        }

        // 参数为空 → 只要不是安装中/完成就显示（不用的场合）
        if (string.IsNullOrEmpty(param))
        {
            return Visibility.Visible;
        }

        return string.Equals(step.ToString(), param, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 把 <see cref="InstallScope"/> 转成 bool，供 RadioButton 的 IsChecked 双向绑定。
/// 参数为 "AllUsers" 时该值为 true。
/// </summary>
public sealed class ScopeToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not InstallScope scope)
        {
            return false;
        }

        var target = parameter as string;
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        return string.Equals(scope.ToString(), target, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool b || !b)
        {
            // 取消勾选时不要改回原值，否则会把「当前用户」误改成「所有用户」
            return Binding.DoNothing;
        }

        var target = parameter as string;
        if (string.Equals(target, "AllUsers", StringComparison.OrdinalIgnoreCase))
        {
            return InstallScope.AllUsers;
        }

        if (string.Equals(target, "CurrentUser", StringComparison.OrdinalIgnoreCase))
        {
            return InstallScope.CurrentUser;
        }

        return Binding.DoNothing;
    }
}

/// <summary>bool → Visibility（支持 "invert" 参数）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → bool 的取反（供 IsEnabled 之类用）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>空字符串 → Collapsed，非空 → Visible。</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
