using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameSaveSync.Infrastructure;

/// <summary>自启动方式。</summary>
public enum AutoStartMethod
{
    /// <summary>不设置自启动（关闭）。</summary>
    None = 0,

    /// <summary>注册表 HKCU Run 项（per-user，不需要管理员）。</summary>
    Registry = 1,

    /// <summary>用户启动文件夹里的快捷方式（shell:startup）。</summary>
    StartupFolder = 2,
}

/// <summary>自启动的检测结果。</summary>
/// <param name="Method">当前生效的方式。</param>
/// <param name="IsConfigured">是否登记了自启动项。</param>
/// <param name="IsEffective">登记的路径是否真实存在且指向当前程序。</param>
/// <param name="Detail">给用户看的原因说明。</param>
/// <param name="RegisteredPath">登记的路径（可能为空）。</param>
public sealed record AutoStartStatus(
    AutoStartMethod Method,
    bool IsConfigured,
    bool IsEffective,
    string Detail,
    string? RegisteredPath)
{
    public static AutoStartStatus Disabled { get; } =
        new(AutoStartMethod.None, false, false, "未设置开机自启", null);
}

/// <summary>
/// 自启动项的读写与检测。
/// <para>
/// 【为什么要检测"是否真实生效"】
/// 只看"注册表里有没有这个键"是不够的：程序换了位置、exe 被删了、
/// 或者有别的软件把它改过，键还在但开机根本起不来。所以检测必须包含
/// <list type="bullet">
/// <item>登记项存在；</item>
/// <item>登记的路径指向的文件真实存在；</item>
/// <item>该路径与当前运行的 exe 是同一个（否则是"别人的旧路径"）。</item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "Reload";
    private const string LegacyValueName = "GameSaveSync";
    private const string ShortcutName = "Reload.lnk";

    /// <summary>
    /// 注册表值名。默认是 "Reload"；测试可以用别的名字，避免动到用户真实的自启项。
    /// </summary>
    public AutoStartService(string? valueName = null)
    {
        ValueName = string.IsNullOrWhiteSpace(valueName) ? DefaultValueName : valueName;
    }

    /// <summary>注册表 Run 项下的值名。</summary>
    public string ValueName { get; }

    /// <summary>启动文件夹快捷方式名（与注册表值名无关，固定）。</summary>
    public const string StartupShortcutFileName = ShortcutName;

    /// <summary>当前运行的 exe 路径（便携模式下也是同一个）。</summary>
    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? string.Empty;

    /// <summary>用户启动文件夹（shell:startup）。</summary>
    public static string StartupFolderPath => Environment.GetFolderPath(
        Environment.SpecialFolder.Startup);

    /// <summary>自启动快捷方式在启动文件夹里的完整路径。</summary>
    public static string StartupShortcutPath =>
        Path.Combine(StartupFolderPath, ShortcutName);

    /// <summary>
    /// 是否处于"已登记自启动"状态（不校验路径是否还有效）。
    /// 保留这个属性是为了兼容托盘菜单与既有调用点。
    /// </summary>
    public bool IsEnabled => GetStatus().IsConfigured;

    /// <summary>
    /// 检测自启动的真实状态。注册表与启动文件夹都查一遍，
    /// 哪个能用就返回哪个（注册表优先，行为更可控）。
    /// </summary>
    public AutoStartStatus GetStatus()
    {
        var current = CurrentExecutablePath;

        var registryValue = ReadRegistryValue();
        if (!string.IsNullOrWhiteSpace(registryValue))
        {
            var (effective, detail) = Evaluate(registryValue, current);
            return new AutoStartStatus(AutoStartMethod.Registry, true, effective, detail, registryValue);
        }

        var shortcut = ReadStartupShortcutTarget();
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            var (effective, detail) = Evaluate(shortcut, current);
            return new AutoStartStatus(AutoStartMethod.StartupFolder, true, effective, detail, shortcut);
        }

        return AutoStartStatus.Disabled;
    }

    /// <summary>
    /// 校验登记路径是否真的能开机启动。
    /// </summary>
    private static (bool Effective, string Detail) Evaluate(string registeredPath, string currentExecutable)
    {
        var path = registeredPath.Trim().Trim('"');

        if (path.Length == 0)
        {
            return (false, "自启动项为空");
        }

        if (!File.Exists(path))
        {
            return (false, $"自启动指向的程序已不存在：{path}");
        }

        if (currentExecutable.Length > 0 &&
            !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(currentExecutable),
                StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"自启动指向的是另一个副本：{path}");
        }

        return (true, $"已生效：{path}");
    }

    /// <summary>
    /// 设置自启动。
    /// <para>
    /// 切换方式时会把另一种方式的登记项清掉，避免开机双开。
    /// <paramref name="enabled"/> 为 false 表示关闭自启动（两种方式都清）。
    /// </para>
    /// </summary>
    public bool TrySet(bool enabled, string? executablePath = null)
    {
        var current = GetStatus();
        var method = current.Method == AutoStartMethod.None
            ? AutoStartMethod.Registry
            : current.Method;

        return TrySet(enabled ? method : AutoStartMethod.None, executablePath);
    }

    /// <summary>按指定方式设置自启动。<see cref="AutoStartMethod.None"/> 表示关闭。</summary>
    public bool TrySet(AutoStartMethod method, string? executablePath = null)
    {
        try
        {
            // 先清掉另一种方式，保证全局只有一种自启动在起作用
            ClearAll();

            if (method == AutoStartMethod.None)
            {
                return true;
            }

            var path = executablePath ?? CurrentExecutablePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            return method switch
            {
                AutoStartMethod.Registry => WriteRegistryValue(path),
                AutoStartMethod.StartupFolder => WriteStartupShortcut(path),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关闭自启动（两种方式都清掉）。</summary>
    public bool TryDisable() => TrySet(AutoStartMethod.None);

    // ============================================================
    // 注册表方式
    // ============================================================

    private string? ReadRegistryValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    private bool WriteRegistryValue(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
        return true;
    }

    private void DeleteRegistryValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 没有旧值就继续
        }
    }

    // ============================================================
    // 启动文件夹方式
    // ============================================================

    /// <summary>
    /// 读取启动文件夹里快捷方式指向的目标。
    /// 用 WScript.Shell COM 解析 .lnk —— .lnk 是二进制格式，纯 .NET 解析不了。
    /// </summary>
    private static string? ReadStartupShortcutTarget()
    {
        var shortcut = StartupShortcutPath;
        if (!File.Exists(shortcut))
        {
            return null;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcut);
            var target = link.TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            // COM 不可用（精简系统 / 沙箱）时退化为"存在即认为已登记"
            return shortcut;
        }
    }

    private static bool WriteStartupShortcut(string path)
    {
        var folder = StartupFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        Directory.CreateDirectory(folder);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return false;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(StartupShortcutPath);
        link.TargetPath = path;
        link.WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        link.Description = "Reload — 游戏存档同步";
        link.Save();
        return true;
    }

    private static void DeleteStartupShortcut()
    {
        try
        {
            var shortcut = StartupShortcutPath;
            if (File.Exists(shortcut))
            {
                File.Delete(shortcut);
            }
        }
        catch
        {
            // 没有就继续
        }
    }

    /// <summary>清掉所有自启动登记（含更名遗留的旧键）。</summary>
    private void ClearAll()
    {
        DeleteRegistryValue();
        DeleteStartupShortcut();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 没有旧值就继续
        }
    }
}
