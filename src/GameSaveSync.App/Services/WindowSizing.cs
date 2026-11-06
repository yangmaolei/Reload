using System.Windows;

namespace GameSaveSync.App.Services;

/// <summary>
/// 高 DPI / 小屏兜底：窗口的 DIP 尺寸必须收进工作区，否则底部按钮会被裁掉。
/// 只在打开时调一次，用户之后手动拖拽不受影响。
/// </summary>
public static class WindowSizing
{
    public static void FitToWorkArea(Window window, double margin = 32)
    {
        ArgumentNullException.ThrowIfNull(window);

        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(640, work.Width - margin);
        var maxHeight = Math.Max(480, work.Height - margin);

        if (window.MinWidth > maxWidth)
        {
            window.MinWidth = maxWidth;
        }

        if (window.MinHeight > maxHeight)
        {
            window.MinHeight = maxHeight;
        }

        window.Width = Math.Min(window.Width, maxWidth);
        window.Height = Math.Min(window.Height, maxHeight);
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        // 显式定位：只靠 CenterScreen 在某些多窗口/DPI 组合下会把窗口推到屏幕外
        window.Left = work.Left + Math.Max(0, (work.Width - window.Width) / 2);
        window.Top = work.Top + Math.Max(0, (work.Height - window.Height) / 2);
    }
}
