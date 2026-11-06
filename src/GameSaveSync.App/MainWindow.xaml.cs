using System.ComponentModel;
using System.Windows;
using GameSaveSync.App.Services;
using GameSaveSync.App.ViewModels;

namespace GameSaveSync.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        WindowSizing.FitToWorkArea(this);
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }


    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.LoadCommand.ExecuteAsync(null);
            await _viewModel.Settings.LoadCommand.ExecuteAsync(null);
            _viewModel.Log.RefreshCommand.Execute(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"加载失败：{ex.Message}", "Reload", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>M2：关窗 = 最小化到托盘，自动备份继续在后台跑。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { IsExiting: false } app)
        {
            e.Cancel = true;
            app.MinimizeToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null)
        {
            return;
        }

        // 最大化时换成"还原"字形
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";

        // 最大化时窗口铺满工作区，保留圆角会在屏幕边缘露出四个缺口。
        // 还原时再把圆角调回来，保证"窗口圆角"外观在普通状态下始终成立。
        ApplyWindowCornerRadius(maximized ? 0 : WindowCornerRadius);
    }

    /// <summary>窗口圆角半径。必须与 XAML 里 WindowChrome.CornerRadius 和根 Border 的 CornerRadius 一致。</summary>
    private const int WindowCornerRadius = 12;

    /// <summary>
    /// 同步设置系统级窗口圆角（WindowChrome）与内容边框圆角。
    /// 两者不一致会出现"边框画在圆外/圆内"的错位。
    /// </summary>
    private void ApplyWindowCornerRadius(int radius)
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CornerRadius = new CornerRadius(radius);
        }

        if (RootBorder is not null)
        {
            RootBorder.CornerRadius = new CornerRadius(radius);
        }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
