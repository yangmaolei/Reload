using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameSaveSync.Setup;

/// <summary>安装过程中的一步。</summary>
public sealed record InstallStep(string Description, Func<CancellationToken, Task>? Action = null);

/// <summary>安装结果。</summary>
public sealed class InstallOutcome
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<string> CompletedSteps { get; } = new();

    public List<string> FailedSteps { get; } = new();
}

/// <summary>
/// 安装执行器。把「做什么」和「怎么做」分开，方便单测覆盖计划与错误处理。
/// </summary>
public sealed class Installer
{
    /// <summary>
    /// 源 exe。
    /// <list type="bullet">
    /// <item>绝对路径 —— 直接用它（开发调试用）</item>
    /// <item>相对路径 —— 按「安装器 exe 所在目录」解析，不能按当前工作目录
    /// （从开始菜单启动时工作目录是别的地方）</item>
    /// </list>
    /// </summary>
    public string SourceExecutable { get; }

    /// <summary>
    /// 嵌入在安装器内部的 payload 资源名。为 null 时不从资源取。
    /// 正式发布的安装器把 Reload.exe 打进 exe 里，用户只需下载一个文件。
    /// </summary>
    public string? EmbeddedResourceName { get; }

    /// <summary>释放出来的临时文件（EmbeddedResourceName 非 null 时有效）。</summary>
    private string? _extractedPayload;

    public Installer(string sourceExecutable, string? embeddedResourceName = null)
    {
        SourceExecutable = ResolveSource(sourceExecutable);
        EmbeddedResourceName = embeddedResourceName;
    }

    /// <summary>
    /// 取得要安装的源 exe 路径。若配置了嵌入资源，先释放到临时目录。
    /// </summary>
    public string GetSourceExecutable()
    {
        if (EmbeddedResourceName is null)
        {
            return SourceExecutable;
        }

        if (_extractedPayload is not null && File.Exists(_extractedPayload))
        {
            return _extractedPayload;
        }

        _extractedPayload = ExtractEmbeddedPayload(EmbeddedResourceName);
        return _extractedPayload;
    }

    /// <summary>把嵌入资源释放到临时目录。</summary>
    private static string ExtractEmbeddedPayload(string resourceName)
    {
        var assembly = typeof(Installer).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var names = assembly.GetManifestResourceNames();
            throw new FileNotFoundException(
                $"安装器内找不到嵌入的 payload（{resourceName}）。实际资源：{string.Join(", ", names)}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "Reload-Setup-Payload");
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, "Reload.exe");

        using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.CopyTo(file);
        }

        return target;
    }

    /// <summary>清理释放出来的临时 payload。</summary>
    public void Cleanup()
    {
        if (_extractedPayload is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_extractedPayload))
            {
                File.Delete(_extractedPayload);
            }
        }
        catch
        {
            // 临时文件删不掉不影响安装结果
        }
    }

    private static string ResolveSource(string sourceExecutable)
    {
        if (Path.IsPathRooted(sourceExecutable))
        {
            return sourceExecutable;
        }

        var baseDirectory = Path.GetDirectoryName(SetupPaths.InstallerExePath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, sourceExecutable));
    }

    /// <summary>构造标准步骤序列。</summary>
    public IReadOnlyList<InstallStep> BuildSteps(InstallPlan plan)
    {
        return new List<InstallStep>
        {
            new("创建安装目录", _ => Task.Run(() =>
            {
                Directory.CreateDirectory(plan.InstallDirectory);
            })),

            new("复制程序文件", _ => Task.Run(() =>
            {
                CopyExecutable(plan);
            })),

            new("创建快捷方式", _ => Task.Run(() =>
            {
                if (!plan.CreateShortcut)
                {
                    return;
                }

                Shortcut.Create(plan.DesktopShortcutPath!, plan.ExecutablePath, plan.InstallDirectory);
                Shortcut.Create(plan.StartMenuShortcutPath!, plan.ExecutablePath, plan.InstallDirectory);
            })),

            new("注册卸载程序", _ => Task.Run(() =>
            {
                RegisterUninstall(plan);
            })),

            new("设置开机自启", _ => Task.Run(() =>
            {
                if (plan.EnableAutoStart)
                {
                    SetAutoStart(plan, true);
                }
            })),
        };
    }

    /// <summary>
    /// 执行安装。每步失败即中止，并记录已完成 / 失败的步骤。
    /// </summary>
    public async Task<InstallOutcome> RunAsync(
        InstallPlan plan,
        IReadOnlyList<InstallStep> steps,
        IProgress<InstallStep>? progress = null,
        CancellationToken ct = default)
    {
        var outcome = new InstallOutcome();

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(step);

            try
            {
                if (step.Action is not null)
                {
                    await step.Action(ct).ConfigureAwait(false);
                }

                outcome.CompletedSteps.Add(step.Description);
            }
            catch (Exception ex)
            {
                outcome.FailedSteps.Add(step.Description);
                outcome.Success = false;
                outcome.Message = $"「{step.Description}」失败：{ex.Message}";
                return outcome;
            }
        }

        outcome.Success = true;
        outcome.Message = "安装完成";
        return outcome;
    }

    /// <summary>
    /// 复制程序文件到目标目录。
    /// </summary>
    private void CopyExecutable(InstallPlan plan)
    {
        var target = plan.ExecutablePath;

        // 目标正被占用时先尝试删掉旧文件（升级场景）
        TryDeleteFile(target);

        var source = GetSourceExecutable();
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>
    /// 写「应用和功能」的卸载项。
    /// </summary>
    private void RegisterUninstall(InstallPlan plan)
    {
        using var key = RegistryKey.OpenBaseKey(
            plan.UninstallHive,
            RegistryView.Registry64).CreateSubKey(plan.UninstallKeyPath, writable: true);

        if (key is null)
        {
            throw new IOException("无法写入卸载注册表项");
        }

        key.SetValue("DisplayName", "Reload — 游戏存档同步");
        key.SetValue("DisplayIcon", plan.ExecutablePath);
        key.SetValue("Publisher", "Reload");
        key.SetValue("InstallLocation", plan.InstallDirectory);
        key.SetValue("UninstallString", plan.UninstallCommand);
        key.SetValue("QuietUninstallString", plan.UninstallCommand);
        key.SetValue("Version", "1.2.0");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
        key.SetValue("EstimatedSize", EstimateSizeInKb());
    }

    /// <summary>估算安装体积（KB），显示在「应用和功能」里。</summary>
    private int EstimateSizeInKb()
    {
        try
        {
            var source = EmbeddedResourceName is null ? SourceExecutable : GetSourceExecutable();
            var info = new FileInfo(source);
            return (int)Math.Max(1, info.Length / 1024);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>写入 / 移除开机自启项。</summary>
    public static void SetAutoStart(InstallPlan plan, bool enabled)
    {
        using var key = RegistryKey.OpenBaseKey(
            plan.AutoStartHive,
            RegistryView.Registry64).CreateSubKey(plan.AutoStartKeyPath, writable: true);

        if (key is null)
        {
            throw new IOException("无法写入开机自启注册表项");
        }

        if (enabled)
        {
            key.SetValue(plan.AutoStartValueName, $"\"{plan.ExecutablePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(plan.AutoStartValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// 读取当前的自启状态，用于安装器启动时回显。
    /// </summary>
    public static bool IsAutoStartEnabled(InstallPlan plan)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(
                plan.AutoStartHive,
                RegistryView.Registry64).OpenSubKey(plan.AutoStartKeyPath, writable: false);

            return key?.GetValue(plan.AutoStartValueName) is string s
                && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 卸载：删文件、删快捷方式、删注册表项。
    /// </summary>
    public static InstallOutcome Uninstall(InstallPlan plan)
    {
        var outcome = new InstallOutcome();
        var problems = new List<string>();

        // 快捷方式
        foreach (var shortcut in new[] { plan.DesktopShortcutPath, plan.StartMenuShortcutPath })
        {
            if (shortcut is null)
            {
                continue;
            }

            TryDeleteFile(shortcut, problems);
        }

        // 主程序（正在运行则先跳过，让用户关掉）
        TryDeleteFile(plan.ExecutablePath, problems);

        // 安装目录（残留文件一并清掉）
        TryDeleteDirectory(plan.InstallDirectory, problems);

        // 注册表
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                plan.UninstallHive,
                RegistryView.Registry64);

            // 整个子键删掉，不是只删默认值
            // （DeleteValue(null) 只会删默认值，键还在，"应用和功能"里依然显示）
            baseKey.DeleteSubKeyTree(plan.UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            problems.Add($"清理卸载注册表项失败：{ex.Message}");
        }

        // 自启项
        try
        {
            using var key = RegistryKey.OpenBaseKey(
                plan.AutoStartHive,
                RegistryView.Registry64).OpenSubKey(plan.AutoStartKeyPath, writable: true);
            key?.DeleteValue(plan.AutoStartValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            problems.Add($"清理开机自启项失败：{ex.Message}");
        }

        if (problems.Count > 0)
        {
            outcome.Success = false;
            outcome.Message = string.Join("\n", problems);
        }
        else
        {
            outcome.Success = true;
            outcome.Message = "卸载完成";
        }

        return outcome;
    }

    private static void TryDeleteFile(string path, List<string>? problems = null)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            problems?.Add($"删除 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string directory, List<string>? problems)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            // 只删我们装的那个目录下的东西，不递归到别处
            foreach (var file in Directory.GetFiles(directory))
            {
                TryDeleteFile(file, problems);
            }

            foreach (var dir in Directory.GetDirectories(directory))
            {
                TryDeleteDirectory(dir, problems);
            }

            Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            problems?.Add($"删除目录 {directory} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 以管理员身份重启自己（用于「所有用户」安装）。
    /// 返回 false 表示用户拒绝了提权。
    /// </summary>
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = SetupPaths.InstallerExePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            // 用户点了「否」
            return false;
        }
    }

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
