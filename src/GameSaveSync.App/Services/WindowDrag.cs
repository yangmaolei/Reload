using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GameSaveSync.App.Services;

/// <summary>
/// 无边框窗口的拖拽移动。
///
/// WPF 里 <c>WindowStyle="None"</c> 的窗口没有系统标题栏，鼠标在窗口上按下不会移动窗口。
/// 常见做法是监听 <c>MouseLeftButtonDown</c> 调 <c>DragMove()</c>，但那样会把
/// 按钮、输入框、列表等交互控件的点击也吃掉（点一下就变成拖窗口）。
///
/// 这里统一处理：只有当鼠标按在"非交互元素"上时才启动拖拽；
/// 交互控件（Button/TextBox/CheckBox/ListBox/ComboBox/ScrollBar…）照常响应点击。
/// </summary>
public static class WindowDrag
{
    /// <summary>
    /// 在鼠标左键按下时尝试拖动窗口；若按在交互控件上则什么都不做。
    /// </summary>
    /// <param name="window">要移动的窗口。</param>
    /// <param name="e">鼠标事件。</param>
    /// <returns>是否启动了拖拽。</returns>
    public static bool TryDrag(Window window, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(e);

        if (e.ChangedButton != MouseButton.Left)
        {
            return false;
        }

        // 按在交互控件上 → 交给控件自己处理（点击/选中），不拖窗口
        if (IsInteractive(e.OriginalSource as DependencyObject))
        {
            return false;
        }

        try
        {
            window.DragMove();
            return true;
        }
        catch (InvalidOperationException)
        {
            // DragMove 在鼠标已释放或窗口正被系统移动时会抛，忽略即可
            return false;
        }
    }

    /// <summary>判断源元素是否属于会消费点击的交互控件。</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        // 从按下点向上回溯视觉树：只要祖先链上有交互控件，就认为这次点击"属于"它
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            switch (current)
            {
                // 注意：C# 要求派生类型排在基类型前面，否则 case 不可达
                // （CheckBox/RadioButton : ToggleButton : ButtonBase；
                //   ListBoxItem : ListBox）
                case CheckBox:
                case RadioButton:
                case ButtonBase:
                case TextBox:
                case PasswordBox:
                case ComboBox:
                case ListBoxItem:
                case ListBox:
                case Slider:
                case ScrollBar:
                case TabControl:
                case ContextMenu:
                case MenuItem:
                    return true;
            }
        }

        return false;
    }
}
