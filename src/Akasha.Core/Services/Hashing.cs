namespace Akasha.Core.Services;

/// <summary>Хеширование паролей и API-ключей через Argon2id (Isopoh.Cryptography.Argon2).</summary>
public static class Hashing
{
    public static string Argon2idHash(string input)
        => Isopoh.Cryptography.Argon2.Argon2.Hash(input);

    public static bool Verify(string input, string hash)
    {
        try { return Isopoh.Cryptography.Argon2.Argon2.Verify(input, hash); }
        catch { return false; }
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
