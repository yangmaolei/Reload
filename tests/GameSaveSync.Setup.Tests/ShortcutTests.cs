using System;
using System.IO;
using System.Text;
using GameSaveSync.Setup;
using Xunit;

namespace GameSaveSync.Setup.Tests;

/// <summary>
/// 快捷方式创建的集成测试（真实 COM 调用）。
/// </summary>
public sealed class ShortcutTests : IDisposable
{
    private readonly string _root;

    public ShortcutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reload-lnk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 忽略
        }
    }

    [Fact]
    public void Create_ProducesShortcutFile()
    {
        var target = Path.Combine(_root, "Reload.exe");
        File.WriteAllBytes(target, new byte[] { 0x4D, 0x5A });
        var lnk = Path.Combine(_root, "Reload.lnk");

        Shortcut.Create(lnk, target, _root);

        Assert.True(File.Exists(lnk), "快捷方式文件未生成");
        Assert.True(new FileInfo(lnk).Length > 0, "快捷方式文件为空");
    }

    [Fact]
    public void Create_MakesParentDirectory()
    {
        var target = Path.Combine(_root, "Reload.exe");
        File.WriteAllBytes(target, new byte[] { 0x4D, 0x5A });
        var lnk = Path.Combine(_root, "nested", "deeper", "Reload.lnk");

        Shortcut.Create(lnk, target, _root);

        Assert.True(File.Exists(lnk));
    }

    [Fact]
    public void Create_OverwritesExisting()
    {
        var target = Path.Combine(_root, "Reload.exe");
        File.WriteAllBytes(target, new byte[] { 0x4D, 0x5A });
        var lnk = Path.Combine(_root, "Reload.lnk");

        Shortcut.Create(lnk, target, _root);
        Shortcut.Create(lnk, target, _root);

        Assert.True(File.Exists(lnk));
    }

    [Fact]
    public void Create_WithEmptyPath_IsNoOp()
    {
        // 不该抛异常
        Shortcut.Create(string.Empty, "x", "y");
    }

    [Fact]
    public void Create_ShortcutIsResolvableByWindowsShell()
    {
        // 文件存在不等于 Windows 能用。用 Shell 解析器读一下目标路径。
        var target = Path.Combine(_root, "Reload.exe");
        File.WriteAllBytes(target, new byte[] { 0x4D, 0x5A });
        var lnk = Path.Combine(_root, "Reload.lnk");

        Shortcut.Create(lnk, target, _root);

        var resolved = ResolveShortcutTarget(lnk);

        Assert.NotNull(resolved);
        Assert.Equal(target, resolved, ignoreCase: true);
    }

    /// <summary>
    /// 用 WScript.Shell 读快捷方式的目标路径。
    /// WScript.Shell 是脚本宿主自动化接口，比直接调 IShellLink 的 vtable 稳。
    /// </summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            var instance = Activator.CreateInstance(shellType);
            if (instance is null)
            {
                return null;
            }

            try
            {
                var link = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null,
                    target: instance,
                    args: new object?[] { lnkPath });

                if (link is null)
                {
                    return null;
                }

                try
                {
                    // IDispatch 成员要用 InvokeMember 读，GetProperty 拿不到
                    var value = link.GetType().InvokeMember(
                        "TargetPath",
                        System.Reflection.BindingFlags.GetProperty,
                        binder: null,
                        target: link,
                        args: Array.Empty<object>());

                    return value as string;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(instance);
            }
        }
        catch
        {
            return null;
        }
    }
}
