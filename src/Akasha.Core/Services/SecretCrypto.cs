using System.Security.Cryptography;

namespace Akasha.Core.Services;

/// <summary>
/// Шифрование секретов: AES-256-GCM. Формат хранения: nonce(12) || ciphertext || tag(16).
/// Ключ: server — AKASHA_MASTER_KEY из ENV; portable — производный от master-password (PBKDF2).
/// </summary>
public static class SecretCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static byte[] DeriveKeyFromMasterKey(string masterKeyEnvValue)
    {
        // Значение ENV может быть base64(32 bytes) или произвольной passphrase — нормализуем через SHA-256.
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(masterKeyEnvValue));
    }

    public static byte[] DeriveKeyFromPassword(string masterPassword)
    {
        using var kdf = new Rfc2898DeriveBytes(masterPassword,
            "akasha-portable-master-kdf-salt"u8.ToArray(), 600_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeySize);
    }

    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var result = new byte[NonceSize + TagSize + plainBytes.Length];
        Span<byte> nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes,
            result.AsSpan(NonceSize + TagSize),
            result.AsSpan(NonceSize, TagSize));
        return result;
    }

    public static string Decrypt(byte[] payload, byte[] key)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid encrypted payload.");
        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
