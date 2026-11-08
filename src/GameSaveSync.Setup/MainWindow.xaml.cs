using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace GameSaveSync.Setup;

/// <summary>
/// 安装向导主窗口（方案 A：左侧品牌导航条 + 步骤指示器）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SetupViewModel _viewModel;

    public MainWindow()
        : this(null)
    {
    }

    /// <summary>
    /// 带提权恢复参数构造。
    /// </summary>
    public MainWindow(SetupArguments? restored)
    {
        InitializeComponent();

        _viewModel = (SetupViewModel)DataContext;

        if (restored is not null)
        {
            _viewModel.Scope = restored.Scope;

            if (!string.IsNullOrWhiteSpace(restored.InstallDirectory))
            {
                _viewModel.InstallDirectory = restored.InstallDirectory!;
            }

            _viewModel.CreateShortcut = restored.CreateShortcut;
            _viewModel.EnableAutoStart = restored.EnableAutoStart;

            // 提权后直接跳到「安装位置」这一步，用户确认后即可安装
            _viewModel.Current = SetupStep.Location;
        }

        // 关掉窗口即退出（安装器没有托盘常驻需求）
        Closed += (_, _) => Application.Current.Shutdown();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        // 完成页：「完成」= 关闭安装器
        if (_viewModel.IsFinished)
        {
            Application.Current.Shutdown();
            return;
        }

        // 「所有用户」需要提权：先自提权重启，参数带上用户的选择
        if (_viewModel.Current == SetupStep.Scope
            && _viewModel.Scope == InstallScope.AllUsers
            && !Installer.IsRunningAsAdministrator())
        {
            var args = $"{SetupPaths.ElevatedArgumentPrefix} " +
                       $"--scope={_viewModel.Scope} " +
                       $"--dir=\"{_viewModel.InstallDirectory}\" " +
                       $"--shortcut={(_viewModel.CreateShortcut ? "1" : "0")} " +
                       $"--autostart={(_viewModel.EnableAutoStart ? "1" : "0")}";

            if (Installer.RelaunchElevated(args))
            {
                Application.Current.Shutdown();
                return;
            }

            MessageBox.Show(
                "需要管理员权限才能安装到「所有用户」。\n\n你可以取消并改选「仅当前用户」，无需任何权限。",
                "Reload 安装程序",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await _viewModel.GoNextAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _viewModel.GoBack();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要取消安装吗？",
            "Reload 安装程序",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Application.Current.Shutdown();
        }
    }
}
