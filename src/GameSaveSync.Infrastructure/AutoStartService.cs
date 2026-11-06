using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GameSaveSync.Infrastructure;

/// <summary>开机自启：写 HKCU\...\Run（per-user，不需要管理员）。</summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Reload";
    private const string LegacyValueName = "GameSaveSync";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TrySet(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            // 更名遗留：清掉旧条目，避免开机双开
            try
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
            catch
            {
                // 没有旧值就继续
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var path = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
