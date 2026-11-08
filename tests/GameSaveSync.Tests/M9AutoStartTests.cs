using GameSaveSync.Infrastructure;

namespace GameSaveSync.Tests;

/// <summary>
/// 自启动检测：验证"登记项存在"与"真实生效"是两回事。
/// <para>
/// 真实生效的三个条件：登记项存在 → 指向的文件存在 → 就是当前这个 exe。
/// 只看注册表里有没有键是不够的：程序换了位置、exe 被删、或者被别的软件改过，
/// 键还在但开机根本起不来。
/// </para>
/// </summary>
public sealed class M7AutoStartTests
{
    /// <summary>
    /// 把 AutoStartService 的判定逻辑抽出来单独测 ——
    /// 真跑注册表会污染用户机器，测试不能这么干。
    /// </summary>
    private static (bool Effective, string Detail) Evaluate(string registeredPath, string currentExecutable)
    {
        var method = typeof(AutoStartService).GetMethod(
            "Evaluate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { registeredPath, currentExecutable });
        Assert.NotNull(result);

        var tuple = ((bool Effective, string Detail))result!;
        return tuple;
    }

    [Fact]
    public void Evaluate_ValidPathToCurrentExe_IsEffective()
    {
        var exe = Path.Combine(Path.GetTempPath(), "reload-autostart-test.exe");
        File.WriteAllText(exe, "fake");

        try
        {
            var (effective, detail) = Evaluate(exe, exe);

            Assert.True(effective);
            Assert.Contains("已生效", detail, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void Evaluate_QuotedPath_IsAccepted()
    {
        var exe = Path.Combine(Path.GetTempPath(), "reload-autostart-quoted.exe");
        File.WriteAllText(exe, "fake");

        try
        {
            // 注册表里存的值通常带引号
            var (effective, _) = Evaluate($"\"{exe}\"", exe);

            Assert.True(effective);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void Evaluate_MissingFile_IsNotEffective()
    {
        var missing = Path.Combine(Path.GetTempPath(), "reload-not-exists-12345.exe");

        var (effective, detail) = Evaluate(missing, missing);

        Assert.False(effective);
        Assert.Contains("不存在", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_DifferentExecutable_IsNotEffective()
    {
        var a = Path.Combine(Path.GetTempPath(), "reload-a.exe");
        var b = Path.Combine(Path.GetTempPath(), "reload-b.exe");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        try
        {
            // 登记的是另一个副本 → 开机起来的不是当前这个程序
            var (effective, detail) = Evaluate(a, b);

            Assert.False(effective);
            Assert.Contains("另一个副本", detail, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void Evaluate_EmptyPath_IsNotEffective()
    {
        var (effective, detail) = Evaluate("   ", Path.Combine(Path.GetTempPath(), "x.exe"));

        Assert.False(effective);
        Assert.Contains("空", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledStatus_IsNotConfiguredAndNotEffective()
    {
        var status = AutoStartStatus.Disabled;

        Assert.False(status.IsConfigured);
        Assert.False(status.IsEffective);
        Assert.Equal(AutoStartMethod.None, status.Method);
        Assert.Null(status.RegisteredPath);
    }

    [Fact]
    public void AutoStartMethod_HasBothRegistryAndStartupFolder()
    {
        // 需求：至少提供两种自启动方式，且可关闭
        Assert.Equal(3, Enum.GetValues<AutoStartMethod>().Length);
        Assert.True(Enum.IsDefined(AutoStartMethod.None));
        Assert.True(Enum.IsDefined(AutoStartMethod.Registry));
        Assert.True(Enum.IsDefined(AutoStartMethod.StartupFolder));
    }

    [Fact]
    public void CurrentExecutablePath_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AutoStartService.CurrentExecutablePath));
    }

    [Fact]
    public void StartupFolderPath_IsResolved()
    {
        var path = AutoStartService.StartupFolderPath;

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("Reload.lnk", AutoStartService.StartupShortcutPath, StringComparison.Ordinal);
    }
}
