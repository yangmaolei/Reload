namespace GameSaveSync.Core.Security;

/// <summary>
/// 凭据保护。实现（Infrastructure.DpapiCredentialProtector）使用 DPAPI CurrentUser 作用域，
/// 密文只落库、绝不写日志。M5 的加密密钥也用同一机制保护。
/// </summary>
public interface ICredentialProtector
{
    /// <summary>加密并返回 base64 密文。</summary>
    string Protect(string plaintext);

    /// <summary>解密 base64 密文；失败抛 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    string Unprotect(string protectedBase64);

    bool IsAvailable { get; }
}
