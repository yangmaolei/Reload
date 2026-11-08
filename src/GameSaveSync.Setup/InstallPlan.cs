using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameSaveSync.Setup;

/// <summary>
/// 安装范围：决定装到哪个目录、要不要提权。
/// </summary>
public enum InstallScope
{
    /// <summary>只给当前用户装，装到用户目录，不需要管理员权限。</summary>
    CurrentUser = 0,

    /// <summary>给所有用户装，装到 Program Files，需要管理员权限。</summary>
    AllUsers = 1,
}

/// <summary>
/// 安装路径与快捷方式的规划结果。纯数据，便于单测。
/// </summary>
public sealed class InstallPlan
{
    public required InstallScope Scope { get; init; }

    /// <summary>程序最终所在目录（不含 exe 名）。</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>目标 exe 的完整路径。</summary>
    public string ExecutablePath => Path.Combine(InstallDirectory, "Reload.exe");

    /// <summary>是否需要管理员权限。</summary>
    public bool RequiresElevation => Scope == InstallScope.AllUsers;

    /// <summary>桌面快捷方式路径。</summary>
    public string? DesktopShortcutPath => CreateShortcut ? DesktopPath : null;

    /// <summary>开始菜单快捷方式路径。</summary>
    public string? StartMenuShortcutPath => CreateShortcut ? StartMenuPath : null;

    public bool CreateShortcut { get; init; } = true;

    /// <summary>是否在安装时打开「开机自启」。</summary>
    public bool EnableAutoStart { get; init; }

    /// <summary>自启动注册表项所在 hive。</summary>
    public Microsoft.Win32.RegistryHive AutoStartHive =>
        Scope == InstallScope.AllUsers
            ? Microsoft.Win32.RegistryHive.LocalMachine
            : Microsoft.Win32.RegistryHive.CurrentUser;

    /// <summary>自启动注册表路径。</summary>
    public string AutoStartKeyPath =>
        AutoStartKeyPathOverride ?? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启动项名称。</summary>
    public string AutoStartValueName =>
        AutoStartValueNameOverride ?? "Reload";

    /// <summary>覆盖自启项名称（测试用）。</summary>
    public string? AutoStartValueNameOverride { get; init; }

    public string DesktopPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Reload.lnk");

    public string StartMenuPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "Reload.lnk");

    /// <summary>「应用和功能」里的卸载项注册表路径。</summary>
    public string UninstallKeyPath =>
        UninstallKeyPathOverride ?? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Reload";

    /// <summary>
    /// 覆盖卸载项注册表路径（测试用，避免动到用户真实配置）。
    /// </summary>
    public string? UninstallKeyPathOverride { get; init; }

    /// <summary>覆盖自启项注册表路径（测试用）。</summary>
    public string? AutoStartKeyPathOverride { get; init; }

    public Microsoft.Win32.RegistryHive UninstallHive =>
        Scope == InstallScope.AllUsers
            ? Microsoft.Win32.RegistryHive.LocalMachine
            : Microsoft.Win32.RegistryHive.CurrentUser;

    /// <summary>卸载命令：指向安装器自身的 --uninstall 模式。</summary>
    public string UninstallCommand => $"\"{SetupPaths.InstallerExePath}\" --uninstall";
}

/// <summary>
/// 安装器的路径常量与默认值。
/// </summary>
public static class SetupPaths
{
    /// <summary>安装器自身 exe 路径（卸载时靠它找到自己）。</summary>
    public static string InstallerExePath { get; } =
        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + "Reload-Setup.exe";

    /// <summary>当前用户安装的默认目录名。</summary>
    public const string CurrentUserFolderName = "Reload";

    /// <summary>所有用户安装的默认目录名。</summary>
    public const string AllUsersFolderName = "Reload";

    /// <summary>卸载命令行参数。</summary>
    public const string UninstallArgument = "--uninstall";

    /// <summary>提权重启时携带的参数前缀。</summary>
    public const string ElevatedArgumentPrefix = "--elevated";

    /// <summary>
    /// 当前用户默认安装目录：%LOCALAPPDATA%\Programs\Reload
    /// （用 Programs 而不是直接放 LOCALAPPDATA 根下，与 VS Code / Discord 一致）
    /// </summary>
    public static string DefaultCurrentUserDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", CurrentUserFolderName);
    }

    /// <summary>
    /// 所有用户默认安装目录：%ProgramFiles%\Reload
    /// </summary>
    public static string DefaultAllUsersDirectory()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, AllUsersFolderName);
    }

    /// <summary>按安装范围给出默认目录。</summary>
    public static string DefaultDirectory(InstallScope scope) =>
        scope == InstallScope.AllUsers
            ? DefaultAllUsersDirectory()
            : DefaultCurrentUserDirectory();

    /// <summary>
    /// 校验用户输入的安装目录是否可用。
    /// </summary>
    public static bool IsDirectoryAcceptable(string directory, out string reason)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            reason = "安装目录不能为空。";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                reason = "安装目录不合法。";
                return false;
            }

            // 装到盘根目录会把一堆文件散在 C:\ 下，挡住
            if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "不能直接安装到磁盘根目录，请选择一个子文件夹。";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"安装目录不合法：{ex.Message}";
            return false;
        }
    }
}
