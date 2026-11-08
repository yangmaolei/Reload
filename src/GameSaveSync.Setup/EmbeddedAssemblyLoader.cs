using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;

namespace GameSaveSync.Setup;

/// <summary>
/// 把依赖 DLL 与 app.config 的绑定重定向一起嵌进安装器，运行时按需还原。
///
/// <para>
/// 【为什么需要】
/// 安装版最好只有一个 exe —— 用户从浏览器下载、拖到任意目录、运行，
/// 不用管旁边的 dll 和 config。但 CommunityToolkit.Mvvm 及其依赖
/// （System.Buffers / System.Memory / ...）是 NuGet 带进来的运行时依赖，
/// 直接发布会散落出 7 个文件。
/// </para>
///
/// <para>
/// 【做法】
/// <list type="number">
/// <item>把 dll 打包成嵌入资源（逻辑名 <c>GameSaveSync.Setup.deps.&lt;名字&gt;.dll</c>）</item>
/// <item>把 app.config 也打包成资源，解析出 bindingRedirect 规则</item>
/// <item>注册 <see cref="AppDomain.AssemblyResolve"/>：CLR 绑定失败时，
/// 先按重定向规则改版本，再从资源释放 dll 并加载</item>
/// </list>
/// </para>
/// </summary>
public static class EmbeddedAssemblyLoader
{
    private const string ResourcePrefix = "GameSaveSync.Setup.deps.";

    private const string ConfigResourceName = "GameSaveSync.Setup.deps.app.config";

    private static readonly object Gate = new();

    private static readonly Dictionary<string, string> ExtractedPaths = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string>? _redirects;

    private static bool _registered;

    /// <summary>注册程序集解析钩子。进程启动时调用一次。</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            _registered = true;
        }
    }

    /// <summary>绑定重定向规则：程序集名 → 目标版本。</summary>
    private static Dictionary<string, string> Redirects => _redirects ??= LoadRedirects();

    /// <summary>从嵌入的 app.config 里读出绑定重定向规则。</summary>
    private static Dictionary<string, string> LoadRedirects()
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = typeof(EmbeddedAssemblyLoader).Assembly.GetManifestResourceStream(ConfigResourceName);
        if (stream is null)
        {
            return redirects;
        }

        var document = new XmlDocument();
        document.Load(stream);

        var nodes = document.SelectNodes("//assemblyBinding/dependentAssembly");
        if (nodes is null)
        {
            return redirects;
        }

        foreach (XmlNode node in nodes)
        {
            var identity = node.SelectSingleNode("assemblyIdentity") as XmlElement;
            var redirect = node.SelectSingleNode("bindingRedirect") as XmlElement;

            if (identity is null || redirect is null)
            {
                continue;
            }

            var name = identity.GetAttribute("name");
            var newVersion = redirect.GetAttribute("newVersion");

            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(newVersion))
            {
                redirects[name] = newVersion;
            }
        }

        return redirects;
    }

    /// <summary>
    /// 解析程序集：从嵌入资源释放 dll 并加载。
    /// </summary>
    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        var simpleName = requested.Name;

        if (string.IsNullOrEmpty(simpleName))
        {
            return null;
        }

        lock (Gate)
        {
            // 已经释放过就直接加载
            if (ExtractedPaths.TryGetValue(simpleName!, out var cached))
            {
                return File.Exists(cached) ? Assembly.LoadFrom(cached) : null;
            }

            var resourceName = ResourcePrefix + simpleName + ".dll";
            using var stream = typeof(EmbeddedAssemblyLoader).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            var directory = Path.Combine(Path.GetTempPath(), "Reload-Setup-Deps");
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, simpleName + ".dll");
            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.CopyTo(file);
            }

            ExtractedPaths[simpleName!] = target;

            // 有绑定重定向规则时，按目标版本加载
            if (Redirects.TryGetValue(simpleName!, out var version)
                && !string.Equals(version, requested.Version?.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                var redirected = new AssemblyName(simpleName!)
                {
                    Version = new Version(version),
                };

                try
                {
                    return Assembly.Load(redirected);
                }
                catch
                {
                    // 退回按文件加载
                }
            }

            return Assembly.LoadFrom(target);
        }
    }

    /// <summary>清理释放出来的临时文件。</summary>
    public static void Cleanup()
    {
        lock (Gate)
        {
            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "Reload-Setup-Deps");
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // 临时文件删不掉不影响使用
            }

            ExtractedPaths.Clear();
        }
    }
}
