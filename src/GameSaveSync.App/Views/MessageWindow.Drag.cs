using System.Windows;
using System.Windows.Input;
using GameSaveSync.App.Services;

namespace GameSaveSync.App.Views;

public partial class MessageWindow : Window
{
    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 用统一助手：按在按钮/输入框上时不触发拖拽，避免吞掉点击
        WindowDrag.TryDrag(this, e);
    }
}
