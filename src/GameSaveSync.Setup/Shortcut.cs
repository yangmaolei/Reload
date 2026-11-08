using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GameSaveSync.Setup;

/// <summary>
/// 创建 Windows 快捷方式（.lnk）。
///
/// <para>
/// 【为什么用 WScript.Shell 而不是 IShellLink】
/// 直接声明 IShellLink 的 COM vtable 一旦顺序错位，
/// <c>IPersistFile.Save</c> 会**静默失败** —— 不抛异常、文件也不生成，
/// 排查极其痛苦。手写 .lnk 二进制格式同样容易在字段偏移上出错。
/// WScript.Shell 是后期绑定的 IDispatch 接口，没有 vtable 顺序问题，
/// 行为与用户在资源管理器里"创建快捷方式"完全一致。
/// </para>
/// </summary>
public static class Shortcut
{
    /// <summary>
    /// 创建一个指向 <paramref name="target"/> 的快捷方式。
    /// </summary>
    public static void Create(string shortcutPath, string target, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法获取 WScript.Shell COM 对象");

        var instance = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法实例化 WScript.Shell");

        object? shortcut = null;

        try
        {
            // COM 的 IDispatch 成员不能用 GetMethod 拿（反射看不到），
            // 必须用 InvokeMember —— 它走 IDispatch 后期绑定。
            var link = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: instance,
                args: new object?[] { shortcutPath });

            if (link is null)
            {
                throw new InvalidOperationException("CreateShortcut 返回空");
            }

            shortcut = link;
            var shortcutType = link.GetType();

            SetProperty(shortcutType, shortcut, "TargetPath", target);
            SetProperty(shortcutType, shortcut, "WorkingDirectory", workingDirectory ?? string.Empty);
            SetProperty(shortcutType, shortcut, "Description", "Reload — 游戏存档同步");
            SetProperty(shortcutType, shortcut, "IconLocation", $"{target},0");

            // Save 是方法不是属性
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: Array.Empty<object>());
        }
        finally
        {
            if (shortcut is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(shortcut);
                }
                catch
                {
                    // 忽略
                }
            }

            try
            {
                Marshal.ReleaseComObject(instance);
            }
            catch
            {
                // 忽略
            }
        }

        if (!File.Exists(shortcutPath))
        {
            throw new IOException($"快捷方式未生成：{shortcutPath}");
        }
    }

    /// <summary>设置 COM 对象的属性，失败不抛（不同 Windows 版本属性集可能不同）。</summary>
    private static void SetProperty(Type type, object instance, string name, string value)
    {
        try
        {
            type.InvokeMember(
                name,
                BindingFlags.SetProperty,
                binder: null,
                target: instance,
                args: new object?[] { value });
        }
        catch
        {
            // 图标位置之类的可选属性设置失败不影响快捷方式可用性
        }
    }
}
