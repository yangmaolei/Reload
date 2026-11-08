using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace GameSaveSync.Setup;

/// <summary>
/// 从注册表反查 Reload 的安装信息（卸载时用）。
/// </summary>
public static class UninstallLocator
{
    private static readonly (RegistryHive Hive, string Path)[] UninstallKeys =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Reload"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Reload"),
    };

    /// <summary>
    /// 找出 Reload 的安装目录。找不到返回 null。
    /// </summary>
    public static string? FindInstallDirectory()
    {
        foreach (var (hive, path) in UninstallKeys)
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
                    .OpenSubKey(path, writable: false);

                var location = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                {
                    return location;
                }
            }
            catch
            {
                // 继续找下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 根据安装目录判断当初是按哪个范围装的。
    /// 装在 Program Files 下 → AllUsers，否则 CurrentUser。
    /// </summary>
    public static InstallScope DetectScope(string installDirectory)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)
                && installDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            {
                return InstallScope.AllUsers;
            }
        }
        catch
        {
            // 判断不了就按当前用户处理
        }

        return InstallScope.CurrentUser;
    }
}
