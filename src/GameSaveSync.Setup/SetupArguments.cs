using System;
using System.Collections.Generic;

namespace GameSaveSync.Setup;

/// <summary>
/// 提权重启时传递的用户选择。
/// </summary>
public sealed class SetupArguments
{
    public InstallScope Scope { get; init; } = InstallScope.CurrentUser;

    public string? InstallDirectory { get; init; }

    public bool CreateShortcut { get; init; } = true;

    public bool EnableAutoStart { get; init; }

    /// <summary>
    /// 从命令行参数恢复选择。
    /// </summary>
    public static SetupArguments Restore(string[] args)
    {
        var scope = InstallScope.CurrentUser;
        string? directory = null;
        var shortcut = true;
        var autoStart = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--scope=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--scope=".Length);
                if (string.Equals(value, nameof(InstallScope.AllUsers), StringComparison.OrdinalIgnoreCase))
                {
                    scope = InstallScope.AllUsers;
                }
            }
            else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
            {
                directory = arg.Substring("--dir=".Length).Trim('"');
            }
            else if (arg.StartsWith("--shortcut=", StringComparison.OrdinalIgnoreCase))
            {
                shortcut = arg.Substring("--shortcut=".Length) == "1";
            }
            else if (arg.StartsWith("--autostart=", StringComparison.OrdinalIgnoreCase))
            {
                autoStart = arg.Substring("--autostart=".Length) == "1";
            }
        }

        return new SetupArguments
        {
            Scope = scope,
            InstallDirectory = directory,
            CreateShortcut = shortcut,
            EnableAutoStart = autoStart,
        };
    }
}
