
using System.Security.Cryptography;

namespace Chat.Server.Auth;

public sealed class PasswordHasher
{
    // Có thể điều chỉnh sau khi đo hiệu năng trên máy chủ thực tế.
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public (byte[] Hash, byte[] Salt, int Iterations) HashPassword(
        string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return (hash, salt, Iterations);
    }

    public bool VerifyPassword(
        string password,
        byte[] salt,
        byte[] expectedHash,
        int iterations)
    {
        if (salt.Length != SaltSize ||
            expectedHash.Length != HashSize ||
            iterations < 100_000 ||
            iterations > 2_000_000)
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(
            actualHash,
            expectedHash);
    }
}
