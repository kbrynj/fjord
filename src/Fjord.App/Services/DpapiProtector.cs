using System.Security.Cryptography;
using System.Text;
using Fjord.Core.Ssh;

namespace Fjord.Services;

/// <summary>Remembered passwords are encrypted with Windows DPAPI — only this Windows user can decrypt them.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Fjord.SSH.v1"u8.ToArray();

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;   // e.g. profiles.json copied from another PC — just ask again
        }
    }
}
