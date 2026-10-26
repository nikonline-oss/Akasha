using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Server.Auth;

/// <summary>Кто выполнил запрос: владелец (session cookie) или агент (Bearer ak_...).</summary>
public record Principal
{
    public bool IsOwner { get; init; }
    public Agent? Agent { get; init; }
    public string Actor => IsOwner ? "owner" : Agent!.Id.ToString();

    public bool HasScope(string scope) => IsOwner || (Agent?.Scopes.Contains(scope) ?? false);

    public static Principal Owner() => new() { IsOwner = true };
    public static Principal FromAgent(Agent a) => new() { IsOwner = false, Agent = a };
}

public interface IAuthService
{
    /// <summary>Проверяет Authorization-заголовок. Возвращает Principal либо null (401).</summary>
    Task<Principal?> AuthenticateAsync(string? authorizationHeader, string? remoteIp, CancellationToken ct = default);
    Task<(string Plaintext, Agent Agent)> CreateAgentAsync(string name, string? description, List<string> scopes, CancellationToken ct = default);
    Task RevokeAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<(string Email, string PlaintextPassword)> EnsureOwnerAsync(CancellationToken ct = default);
    bool VerifyOwnerPassword(string email, string password);
}

public class AuthService : IAuthService
{
    private readonly IDbContextFactoryLike _factory;
    private readonly IConfiguration _config;

    public AuthService(IDbContextFactoryLike factory, IConfiguration config)
    {
        _factory = factory;
        _config = config;
    }

    public async Task<Principal?> AuthenticateAsync(string? authorizationHeader, string? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        var parsed = Hashing.ParseAgentKey(authorizationHeader);
        if (parsed is null) return null;
        var (prefix, secret) = parsed.Value;

        using var db = _factory.Create();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.KeyPrefix == prefix && a.RevokedAt == null, ct);
        if (agent is null || !Hashing.Verify(secret, agent.KeyHash)) return null;

        agent.LastUsedAt = DateTimeOffset.UtcNow;
        agent.LastUsedIp = remoteIp;
        await db.SaveChangesAsync(ct);
        return Principal.FromAgent(agent);
    }

    public async Task<(string Plaintext, Agent Agent)> CreateAgentAsync(string name, string? description,
        List<string> scopes, CancellationToken ct = default)
    {
        scopes = scopes.Distinct().ToList();
        foreach (var s in scopes)
            if (!Scopes.IsValid(s))
                throw new ArgumentException($"invalid scope '{s}'");
        if (scopes.Count == 0) scopes.Add(Scopes.Read);

        var (plaintext, prefix, keyHash) = Hashing.GenerateAgentKey();
        var agent = new Agent
        {
            Name = name, Description = description,
            KeyPrefix = prefix, KeyHash = keyHash, Scopes = scopes,
        };
        using var db = _factory.Create();
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return (plaintext, agent);
    }

    public async Task RevokeAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        using var db = _factory.Create();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null) throw new KeyNotFoundException("agent not found");
        agent.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Создаёт владельца с паролем из ENV (AKASHA_OWNER_EMAIL / AKASHA_OWNER_PASSWORD), если ещё нет.</summary>
    public async Task<(string Email, string PlaintextPassword)> EnsureOwnerAsync(CancellationToken ct = default)
    {
        using var db = _factory.Create();
        var owner = await db.Owners.FirstOrDefaultAsync(ct);
        if (owner is not null) return (owner.Email ?? "", "");

        var email = _config["AKASHA_OWNER_EMAIL"] ?? "owner@localhost";
        var password = _config["AKASHA_OWNER_PASSWORD"] ?? Hashing.GenerateBase62Secret(16);
        db.Owners.Add(new Owner
        {
            Email = email,
            PasswordHash = Hashing.Argon2idHash(password),
            DisplayName = email.Split('@')[0],
        });
        await db.SaveChangesAsync(ct);
        return (email, password);
    }

    public bool VerifyOwnerPassword(string email, string password)
    {
        using var db = _factory.Create();
        var owner = db.Owners.FirstOrDefault(o => o.Email == email);
        return owner?.PasswordHash is not null && Hashing.Verify(password, owner.PasswordHash);
    }
}
