using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace Fjord.Core.Keys;

public enum KeyAlgorithm
{
    /// <summary>Modern default for OpenSSH (ssh-keygen without -t since OpenSSH 9.5).</summary>
    Ed25519,
    /// <summary>For old servers/devices that don't support Ed25519.</summary>
    Rsa4096,
}

public sealed record GeneratedKey(
    KeyAlgorithm Algorithm,
    string PrivateKeyText,   // "-----BEGIN OPENSSH PRIVATE KEY-----" (openssh-key-v1)
    string PublicKeyLine,    // "ssh-ed25519 AAAA... comment" — what goes into authorized_keys
    string Fingerprint,      // "SHA256:..." — same as `ssh-keygen -lf`
    bool IsEncrypted);

/// <summary>
/// Generates key pairs in the native OpenSSH format (openssh-key-v1), the same format
/// `ssh-keygen` writes by default. Not PEM/PKCS#1, not PuTTY .ppk.
/// Passphrase protection uses aes256-ctr + bcrypt_pbkdf, exactly like ssh-keygen.
/// </summary>
public static class OpenSshKeyGenerator
{
    private const string AuthMagic = "openssh-key-v1\0";
    private const int KdfRounds = 16;   // ssh-keygen default (-a 16)

    public static GeneratedKey Generate(KeyAlgorithm algorithm, string comment, string? passphrase)
    {
        comment = SanitizeComment(comment);

        byte[] publicBlob;
        byte[] privateFields; // everything in the private section between the check ints and the comment

        switch (algorithm)
        {
            case KeyAlgorithm.Ed25519:
            {
                var seed = RandomNumberGenerator.GetBytes(32);
                var priv = new Ed25519PrivateKeyParameters(seed);
                var pub = priv.GeneratePublicKey().GetEncoded();

                publicBlob = new SshWireWriter().String("ssh-ed25519").String(pub).ToArray();
                privateFields = new SshWireWriter()
                    .String("ssh-ed25519")
                    .String(pub)
                    .String([.. seed, .. pub])   // OpenSSH stores 64 bytes: seed || public key
                    .ToArray();
                CryptographicOperations.ZeroMemory(seed);
                break;
            }
            case KeyAlgorithm.Rsa4096:
            {
                using var rsa = RSA.Create(4096);
                var p = rsa.ExportParameters(includePrivateParameters: true);
                publicBlob = new SshWireWriter().String("ssh-rsa").MPInt(p.Exponent).MPInt(p.Modulus).ToArray();
                privateFields = new SshWireWriter()
                    .String("ssh-rsa")
                    .MPInt(p.Modulus).MPInt(p.Exponent).MPInt(p.D)
                    .MPInt(p.InverseQ)   // iqmp = q^-1 mod p
                    .MPInt(p.P).MPInt(p.Q)
                    .ToArray();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        bool encrypt = !string.IsNullOrEmpty(passphrase);
        int blockSize = encrypt ? 16 : 8;

        // Private section: checkint x2, key fields, comment, padding 1,2,3...
        var check = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        var section = new SshWireWriter().UInt32(check).UInt32(check).Raw(privateFields).String(comment);
        for (byte pad = 1; section.Length % blockSize != 0; pad++)
            section.Raw([pad]);
        var plain = section.ToArray();

        string cipherName = "none", kdfName = "none";
        byte[] kdfOptions = [];
        byte[] privateSection = plain;

        if (encrypt)
        {
            cipherName = "aes256-ctr";
            kdfName = "bcrypt";
            var salt = RandomNumberGenerator.GetBytes(16);
            kdfOptions = new SshWireWriter().String(salt).UInt32(KdfRounds).ToArray();

            var keyIv = new byte[48];
            new BCrypt().Pbkdf(Encoding.UTF8.GetBytes(passphrase!), salt, KdfRounds, keyIv);
            privateSection = Aes256Ctr(keyIv.AsSpan(0, 32), keyIv.AsSpan(32, 16), plain);
            CryptographicOperations.ZeroMemory(keyIv);
        }

        var file = new SshWireWriter()
            .Raw(Encoding.ASCII.GetBytes(AuthMagic))
            .String(cipherName)
            .String(kdfName)
            .String(kdfOptions)
            .UInt32(1)                 // number of keys
            .String(publicBlob)
            .String(privateSection)
            .ToArray();
        CryptographicOperations.ZeroMemory(plain);

        var b64 = Convert.ToBase64String(file);
        var sb = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < b64.Length; i += 70)
            sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        sb.Append("-----END OPENSSH PRIVATE KEY-----\n");

        var keyType = algorithm == KeyAlgorithm.Ed25519 ? "ssh-ed25519" : "ssh-rsa";
        var publicLine = $"{keyType} {Convert.ToBase64String(publicBlob)}" + (comment.Length > 0 ? " " + comment : "");

        return new GeneratedKey(algorithm, sb.ToString(), publicLine, Fingerprint(publicBlob), encrypt);
    }

    /// <summary>SHA256 fingerprint as printed by `ssh-keygen -lf` (base64 without padding).</summary>
    public static string Fingerprint(byte[] publicBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

    /// <summary>Default file name ssh-keygen would use, e.g. id_ed25519.</summary>
    public static string DefaultFileName(KeyAlgorithm algorithm) =>
        algorithm == KeyAlgorithm.Ed25519 ? "id_ed25519" : "id_rsa";

    public static string DefaultSshDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Writes "path" (private) and "path.pub" (public). Locks down the private key's permissions.</summary>
    public static void Save(GeneratedKey key, string privateKeyPath, bool overwrite)
    {
        var pubPath = privateKeyPath + ".pub";
        if (!overwrite && (File.Exists(privateKeyPath) || File.Exists(pubPath)))
            throw new IOException($"'{privateKeyPath}' already exists.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
        File.WriteAllText(privateKeyPath, key.PrivateKeyText, new UTF8Encoding(false));
        RestrictToCurrentUser(privateKeyPath);
        File.WriteAllText(pubPath, key.PublicKeyLine + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Windows OpenSSH (ssh.exe) refuses private keys that other users can read,
    /// so we break ACL inheritance and grant only the current user. chmod 600 elsewhere.
    /// </summary>
    public static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Comments end up in authorized_keys and shell commands, so keep them simple.</summary>
    public static string SanitizeComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return "";
        var sb = new StringBuilder();
        foreach (var c in comment.Trim())
            if (char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_' or ' ' or '+' or ':')
                sb.Append(c);
        return sb.ToString();
    }

    private static byte[] Aes256Ctr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        var counter = iv.ToArray();
        var keystream = new byte[16];
        var output = new byte[data.Length];

        for (int offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int n = Math.Min(16, data.Length - offset);
            for (int i = 0; i < n; i++)
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);

            for (int i = 15; i >= 0; i--)   // big-endian 128-bit increment
                if (++counter[i] != 0) break;
        }
        return output;
    }
}
