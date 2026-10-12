using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace TallyNext.Infrastructure.Local.Security;

/// <summary>Encrypts small secrets at rest with an OS-managed key.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext, string purpose);

    byte[] Unprotect(byte[] protectedData, string purpose);
}

/// <summary>
/// Windows Data Protection API, current-user scope: only the same Windows account on the same machine can decrypt.
/// The purpose string is used as additional entropy so one secret cannot be substituted for another.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext, string purpose) =>
        ProtectedData.Protect(plaintext, Entropy(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData, string purpose) =>
        ProtectedData.Unprotect(protectedData, Entropy(purpose), DataProtectionScope.CurrentUser);

    private static byte[] Entropy(string purpose) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("TallyNext/" + purpose));
}

/// <summary>
/// Offline credential verifier: PBKDF2-HMAC-SHA256 with a random salt. Stores no password and nothing from which
/// the server credential could be used; it can only confirm a password typed on this device.
/// </summary>
public static class OfflineVerifier
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static (byte[] Salt, byte[] Hash) Create(string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return (salt, Derive(password, salt, iterations));
    }

    public static bool Verify(string password, byte[] salt, byte[] expectedHash, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var actual = Derive(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
}
