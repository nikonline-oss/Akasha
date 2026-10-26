namespace Akasha.Core.Services;

/// <summary>Хеширование паролей и API-ключей через Argon2id (Konscious.Security.Cryptography).</summary>
public static class Hashing
{
    // Параметры Argon2id: m=64MiB, t=3, p=1 (OWASP-совместимо для серверных приложений).
    private const int MemorySizeKiB = 65536;
    private const int Iterations = 3;
    private const int DegreeOfParallelism = 1;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public static string Argon2idHash(string input)
    {
        var salt = new byte[SaltLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var hash = ComputeArgon2id(input, salt);
        return "$argon2id$v=19$m=" + MemorySizeKiB + ",t=" + Iterations + ",p=" + DegreeOfParallelism
               + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
    }

    public static bool Verify(string input, string hash)
    {
        try
        {
            var parts = hash.Split('$');
            // "", "argon2id", "v=19", "m=..,t=..,p=..", saltB64, hashB64
            if (parts.Length != 6 || parts[1] != "argon2id") return false;
            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);
            var actual = ComputeArgon2id(input, salt);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static byte[] ComputeArgon2id(string input, byte[] salt)
    {
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(
            System.Text.Encoding.UTF8.GetBytes(input))
        {
            Salt = salt,
            DegreeOfParallelism = DegreeOfParallelism,
            MemorySize = MemorySizeKiB,
            Iterations = Iterations
        };
        return argon2.GetBytes(HashLength);
    }

    private const string Base62 = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Генерирует секрет длиной <paramref name="length"/> из base62.</summary>
    public static string GenerateBase62Secret(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var sb = new System.Text.StringBuilder(length);
        foreach (var b in bytes) sb.Append(Base62[b % Base62.Length]);
        return sb.ToString();
    }

    /// <summary>
    /// Формат ключа: ak_&lt;8 hex prefix&gt;_&lt;32 base62 secret&gt;.
    /// Возвращает (plaintext, prefix, argon2id(secret)).
    /// </summary>
    public static (string Plaintext, string Prefix, string KeyHash) GenerateAgentKey()
    {
        var prefixBytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(prefixBytes);
        var prefix = Convert.ToHexString(prefixBytes).ToLowerInvariant(); // 8 hex
        var secret = GenerateBase62Secret(32);
        var plaintext = $"ak_{prefix}_{secret}";
        return (plaintext, prefix, Argon2idHash(secret));
    }

    /// <summary>Разбирает заголовок Bearer ak_prefix_secret.</summary>
    public static (string prefix, string secret)? ParseAgentKey(string authorizationHeader)
    {
        const string bearer = "Bearer ";
        if (authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            authorizationHeader = authorizationHeader[bearer.Length..].Trim();
        if (!authorizationHeader.StartsWith("ak_")) return null;
        var rest = authorizationHeader[3..];
        var idx = rest.IndexOf('_');
        if (idx <= 0 || idx == rest.Length - 1) return null;
        return (rest[..idx], rest[(idx + 1)..]);
    }
}
