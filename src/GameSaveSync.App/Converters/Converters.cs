using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GameSaveSync.App.ViewModels;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Restore;

namespace GameSaveSync.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>空状态：计数为 0 时显示占位内容；参数 invert 取反。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int number ? number : 0;
        var visible = count == 0;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → 隐藏（参数 invert 时 null → 显示）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>空字符串 → 隐藏。</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>主壳页面切换：参数 games / diagnostics / settings。</summary>
public sealed class PageToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var page = value?.ToString() ?? string.Empty;
        var expected = parameter as string ?? string.Empty;

        // 绑定参数缺失时（两边都是空串）不能判等 → 否则会错误地显示某个页面
        if (expected.Length == 0)
        {
            return Visibility.Collapsed;
        }

        return page.Equals(expected, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>状态徽标：MD3 的 success / warning / error 容器色，文字用对应 on-*。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly (Brush Background, Brush Foreground) Synced = Create("#B7F0C4", "#00210B");
    private static readonly (Brush Background, Brush Foreground) Pending = Create("#FFDF9E", "#261A00");
    private static readonly (Brush Background, Brush Foreground) Danger = Create("#FFDAD6", "#410002");
    private static readonly (Brush Background, Brush Foreground) Neutral = Create("#DDE4E3", "#3F4949");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is GameSyncState syncState ? syncState : GameSyncState.Neutral;
        var palette = state switch
        {
            GameSyncState.Synced => Synced,
            GameSyncState.Pending => Pending,
            GameSyncState.Conflict => Danger,
            GameSyncState.NeedsRelocate => Pending,
            GameSyncState.Failed => Danger,
            _ => Neutral,
        };

        var wantsForeground = parameter is string which &&
                              which.Equals("foreground", StringComparison.OrdinalIgnoreCase);
        return wantsForeground ? palette.Foreground : palette.Background;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static (Brush Background, Brush Foreground) Create(string background, string foreground) =>
        (new SolidColorBrush((Color)ColorConverter.ConvertFromString(background)!),
         new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground)!));
}

/// <summary>快照触发原因 → 图标字形（Segoe MDL2 Assets）。</summary>
public sealed class TriggerGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var trigger = value is SnapshotTrigger snapshotTrigger ? snapshotTrigger : SnapshotTrigger.Manual;
        return trigger switch
        {
            SnapshotTrigger.Exit => "\uE7E8",        // 退出
            SnapshotTrigger.PreLaunch => "\uE768",   // 启动前
            SnapshotTrigger.Idle => "\uE823",        // 空闲
            SnapshotTrigger.PostChange => "\uE8A5",  // 文件变更
            SnapshotTrigger.PreRestore => "\uE777",  // 恢复前
            _ => "\uE74E",                            // 手动
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>封面缺失时的首字母（中文取首字，英文取前两个词首字母）。</summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var trimmed = name.Trim();
        var words = trimmed.Split(new[] { ' ', '-', ':', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Length > 0 && words[1].Length > 0 &&
            char.IsAsciiLetterOrDigit(words[0][0]) && char.IsAsciiLetterOrDigit(words[1][0]))
        {
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        }

        return trimmed[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>名字 → 一个稳定的 MD3 容器色，用于没有封面时的色块。</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly string[] Backgrounds = { "#9CF1F5", "#CCE8E9", "#D3E4FF", "#FFDAD6", "#FFDF9E", "#EADDFF" };
    private static readonly string[] Foregrounds = { "#002022", "#051F20", "#041C35", "#410002", "#261A00", "#21005D" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;
        var hash = 0;
        foreach (var c in name)
        {
            hash = unchecked(hash * 31 + c);
        }

        // 不能用 Math.Abs(hash)：hash 恰为 int.MinValue 时会抛 OverflowException
        // （如游戏名 "Gycxh!"），卡片渲染直接崩。转成无符号再取模即可。
        var index = (int)((uint)hash % (uint)Backgrounds.Length);
        var wantsForeground = parameter is string which &&
                              which.Equals("foreground", StringComparison.OrdinalIgnoreCase);

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            wantsForeground ? Foregrounds[index] : Backgrounds[index])!);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>差异条目的颜色：新增=绿、修改=琥珀、删除=红。</summary>
public sealed class DiffKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is DiffChangeKind change ? change : DiffChangeKind.Added;
        var wantsForeground = parameter is string which &&
                              which.Equals("foreground", StringComparison.OrdinalIgnoreCase);

        var (background, foreground) = kind switch
        {
            DiffChangeKind.Added => ("#B7F0C4", "#00210B"),
            DiffChangeKind.Modified => ("#FFDF9E", "#261A00"),
            _ => ("#FFDAD6", "#410002"),
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            wantsForeground ? foreground : background)!);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>字节数 → 人类可读（诊断/差异视图用）。</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0L,
        };

        return DiagnosticsFormatting.FormatBytes(bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>#RRGGBB / #AARRGGBB 字符串 → SolidColorBrush。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.Transparent;
        }

        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>日志级别配色：错误红、警告琥珀、信息青、调试灰。</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value as string ?? string.Empty;
        var color = level switch
        {
            "错误" => "#BA1A1A",
            "警告" => "#7A5900",
            "信息" => "#00696D",
            _ => "#6F7979",
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal static class DiagnosticsFormatting
{
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
