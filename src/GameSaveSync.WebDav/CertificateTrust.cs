using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace GameSaveSync.WebDav;

/// <summary>
/// 证书信任判定（单独抽出来便于单测）。
/// 原则：只有"证书链校验通过"或"用户显式信任的 SHA-256 指纹一致"才放行，
/// 绝不做全局跳过校验。
/// </summary>
public static class CertificateTrust
{
    public static bool IsTrusted(
        X509Certificate2? certificate,
        SslPolicyErrors errors,
        string? pinnedThumbprint)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        var pinned = NormalizeThumbprint(pinnedThumbprint);
        if (pinned is null)
        {
            return false;
        }

        var actual = NormalizeThumbprint(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        return string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        var cleaned = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
        return cleaned.Length == 0 ? null : cleaned.ToUpperInvariant();
    }
}
