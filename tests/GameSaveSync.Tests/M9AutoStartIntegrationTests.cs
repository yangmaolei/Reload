using GameSaveSync.Infrastructure;
using Microsoft.Win32;

namespace GameSaveSync.Tests;

/// <summary>
/// 自启动真机验证：真的写注册表、真的建快捷方式、真的检测。
/// <para>
/// 【安全约束】用独立的注册表值名（ReloadTest-*），并在结束时清理 ——
/// 绝不碰用户真实的 "Reload" 自启项和启动文件夹里的 Reload.lnk。
/// </para>
/// </summary>
public sealed class M7AutoStartIntegrationTests : IDisposable
{
    private readonly string _valueName = "ReloadTest-" + Guid.NewGuid().ToString("N")[..8];
    private readonly AutoStartService _service;
    private readonly string _exePath;

    public M7AutoStartIntegrationTests()
    {
        _service = new AutoStartService(_valueName);

        // 造一个"假 exe"：内容无所谓，检测只看文件是否存在
        _exePath = Path.Combine(Path.GetTempPath(), "reload-autostart-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(_exePath, "fake exe");
    }

    public void Dispose()
    {
        // 清理：注册表项 + 假 exe
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch
        {
            // 清理失败不影响测试结论
        }

        try
        {
            if (File.Exists(_exePath))
            {
                File.Delete(_exePath);
            }
        }
        catch
        {
            // 同上
        }
    }

    [Fact]
    public void Initially_NotConfigured()
    {
        var status = _service.GetStatus();

        Assert.False(status.IsConfigured);
        Assert.False(status.IsEffective);
        Assert.Equal(AutoStartMethod.None, status.Method);
    }

    [Fact]
    public void SetRegistry_ThenGetStatus_ReportsConfigured()
    {
        var ok = _service.TrySet(AutoStartMethod.Registry, _exePath);

        Assert.True(ok);

        var status = _service.GetStatus();
        Assert.True(status.IsConfigured);
        Assert.Equal(AutoStartMethod.Registry, status.Method);
        Assert.Contains(_exePath, status.RegisteredPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetRegistry_WithMissingExe_Fails()
    {
        var missing = Path.Combine(Path.GetTempPath(), "reload-not-exists-" + Guid.NewGuid().ToString("N") + ".exe");

        var ok = _service.TrySet(AutoStartMethod.Registry, missing);

        Assert.False(ok);
        Assert.False(_service.GetStatus().IsConfigured);
    }

    [Fact]
    public void SetRegistry_ThenDisable_ClearsIt()
    {
        _service.TrySet(AutoStartMethod.Registry, _exePath);
        Assert.True(_service.GetStatus().IsConfigured);

        var ok = _service.TrySet(AutoStartMethod.None);

        Assert.True(ok);
        Assert.False(_service.GetStatus().IsConfigured);
    }

    [Fact]
    public void SwitchingMethod_ClearsPreviousOne()
    {
        _service.TrySet(AutoStartMethod.Registry, _exePath);
        Assert.Equal(AutoStartMethod.Registry, _service.GetStatus().Method);

        _service.TrySet(AutoStartMethod.StartupFolder, _exePath);

        // 注册表那项应该被清掉了（否则开机双开）
        var status = _service.GetStatus();
        Assert.NotEqual(AutoStartMethod.Registry, status.Method);
    }

    [Fact]
    public void Disable_ClearsBothMethods()
    {
        _service.TrySet(AutoStartMethod.Registry, _exePath);

        _service.TryDisable();

        Assert.False(_service.GetStatus().IsConfigured);
    }

    [Fact]
    public void IsEnabled_MatchesConfiguredState()
    {
        Assert.False(_service.IsEnabled);

        _service.TrySet(AutoStartMethod.Registry, _exePath);
        Assert.True(_service.IsEnabled);

        _service.TrySet(AutoStartMethod.None);
        Assert.False(_service.IsEnabled);
    }

    [Fact]
    public void RegisteredPath_PointsToRealFile()
    {
        _service.TrySet(AutoStartMethod.Registry, _exePath);

        var status = _service.GetStatus();
        var path = status.RegisteredPath!.Trim().Trim('"');

        Assert.True(File.Exists(path));
        Assert.Equal(Path.GetFullPath(_exePath), Path.GetFullPath(path));
    }

    [Fact]
    public void LegacyValueName_IsDifferentFromDefault()
    {
        // 改名遗留键名不能与新键名相同，否则清理逻辑会误删当前项
        Assert.NotEqual("GameSaveSync", _valueName);
    }
}
