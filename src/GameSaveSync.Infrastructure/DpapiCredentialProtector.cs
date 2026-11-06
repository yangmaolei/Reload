using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GameSaveSync.Core.Security;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// DPAPI（CurrentUser 作用域）凭据保护。密文可在同一用户下次启动时解密，
/// 换用户/换机器不可解 —— 正好符合"WebDAV 凭据只属于本机本用户"。
/// 额外 entropy 与程序集绑定，避免同用户下其他程序直接解出。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameSaveSync.WebDav.v1");

    public static DpapiCredentialProtector Instance { get; } = new();

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public string Unprotect(string protectedBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedBase64);
        var cipher = Convert.FromBase64String(protectedBase64);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
