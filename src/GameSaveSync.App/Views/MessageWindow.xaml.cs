using System.Windows;

namespace GameSaveSync.App.Views;

/// <summary>
/// MD3 风格的消息对话框（替代系统 MessageBox，保证整机视觉一致）。
/// 用法：<c>MessageWindow.Info(owner, title, message)</c>。
/// </summary>
public partial class MessageWindow : Window
{
    public enum Kind
    {
        Info,
        Warning,
        Error,
    }

    private MessageWindow()
    {
        InitializeComponent();
    }

    public static void Info(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, Kind.Info, confirm: false, "知道了", null);

    public static void Error(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, Kind.Error, confirm: false, "知道了", null);

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string okText,
        string cancelText,
        Kind kind = Kind.Warning) =>
        ShowCore(owner, title, message, kind, confirm: true, okText, cancelText);

    private static bool ShowCore(
        Window? owner,
        string title,
        string message,
        Kind kind,
        bool confirm,
        string okText,
        string? cancelText)
    {
        var window = new MessageWindow
        {
            Owner = owner,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40),
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.OkButton.Content = okText;
        window.CancelButton.Content = cancelText ?? "取消";
        window.CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;

        switch (kind)
        {
            case Kind.Error:
                window.IconText.Text = "\uEA39";
                window.IconText.Foreground = (System.Windows.Media.Brush)window.FindResource("Md3.Error");
                window.IconHost.Background = (System.Windows.Media.Brush)window.FindResource("Md3.ErrorContainer");
                break;
            case Kind.Warning:
                window.IconText.Text = "\uE7BA";
                window.IconText.Foreground = (System.Windows.Media.Brush)window.FindResource("Md3.OnWarningContainer");
                window.IconHost.Background = (System.Windows.Media.Brush)window.FindResource("Md3.WarningContainer");
                break;
            default:
                window.IconText.Text = "\uE946";
                window.IconText.Foreground = (System.Windows.Media.Brush)window.FindResource("Md3.OnPrimaryContainer");
                window.IconHost.Background = (System.Windows.Media.Brush)window.FindResource("Md3.PrimaryContainer");
                break;
        }

        return window.ShowDialog() == true;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
