using System;
using System.Windows;
using GameSaveSync.Setup;

namespace GameSaveSync.Setup;

/// <summary>
/// 安装器入口。
/// 支持两种命令行模式：
/// <list type="bullet">
/// <item>无参数 —— 走安装向导</item>
/// <item><c>--uninstall</c> —— 直接执行卸载</item>
/// </list>
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先注册依赖 dll 的按需加载（单文件安装器用）
        EmbeddedAssemblyLoader.Register();

        // 退出时清掉释放出来的临时文件
        this.Exit += (_, _) => EmbeddedAssemblyLoader.Cleanup();

        var args = e.Args ?? Array.Empty<string>();

        if (HasFlag(args, SetupPaths.UninstallArgument))
        {
            RunUninstall();
            return;
        }

        // 「所有用户」安装需要提权 —— 用 runas 重启自己
        if (HasFlag(args, SetupPaths.ElevatedArgumentPrefix))
        {
            // 提权后已处于管理员状态，恢复用户先前的选择并继续正常流程
            var restored = SetupArguments.Restore(args);
            Dispatcher.Invoke(() => new MainWindow(restored).Show());
            return;
        }

        Dispatcher.Invoke(() => new MainWindow().Show());
    }

    /// <summary>执行卸载并退出。</summary>
    private static void RunUninstall()
    {
        var result = MessageBox.Show(
            "确定要卸载 Reload 吗？\n\n将删除程序文件、快捷方式及相关注册表项。",
            "卸载 Reload",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            Application.Current.Shutdown();
            return;
        }

        // 卸载前先把正在运行的 Reload 关掉，否则文件被占用删不掉
        TryStopRunningApp();

        // 从注册表反查安装目录（卸载命令只带安装器路径，不知道装到哪了）
        var directory = UninstallLocator.FindInstallDirectory();
        if (directory is null)
        {
            MessageBox.Show(
                "没有找到 Reload 的安装记录，可能已经卸载过了。",
                "卸载 Reload",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Application.Current.Shutdown();
            return;
        }

        var scope = UninstallLocator.DetectScope(directory);
        var plan = new InstallPlan
        {
            Scope = scope,
            InstallDirectory = directory,
            CreateShortcut = true,
            EnableAutoStart = false,
        };

        var outcome = Installer.Uninstall(plan);

        // 卸载后清掉临时文件
        EmbeddedAssemblyLoader.Cleanup();
        CleanupPayload();

        MessageBox.Show(
            outcome.Success ? "Reload 已卸载完成。" : $"卸载过程中有问题：\n\n{outcome.Message}",
            "卸载 Reload",
            MessageBoxButton.OK,
            outcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Application.Current.Shutdown();
    }

    /// <summary>清掉释放出来的临时 payload。</summary>
    private static void CleanupPayload()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Reload-Setup-Payload");
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响卸载结果
        }
    }

    /// <summary>结束正在运行的 Reload 进程。</summary>
    private static void TryStopRunningApp()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Reload");
            foreach (var process in processes)
            {
                try
                {
                    process.CloseMainWindow();

                    // 给它一点时间自己退出
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // 没权限就算了，让后面的删除去报告问题
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
        {
            if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
