using System.Collections.Concurrent;
using System.Text.Json;
using Akasha.Core;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Akasha.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Server.Api;

/// <summary>Хранилище session-куков владельца (in-memory; server mode).</summary>
public static class SessionStore
{
    private static readonly ConcurrentDictionary<string, (string Email, DateTimeOffset Expires)> Sessions = new();

    public static string Issue(string email)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Sessions[token] = (email, DateTimeOffset.UtcNow.AddDays(30));
        return token;
    }

    public static bool TryValidate(string token, out string email)
    {
        if (Sessions.TryGetValue(token, out var s) && s.Expires > DateTimeOffset.UtcNow)
        {
            email = s.Email;
            return true;
        }
        email = "";
        return false;
    }
}

/// <summary>
/// Middleware аутентификации (§5.1/§6): Bearer ak_... → агент; session cookie → owner;
/// portable-режим → owner без пароля (только localhost). Плюс rate limiting (§6.4).
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _portable;

    public AuthMiddleware(RequestDelegate next, bool portable = false)
    {
        _next = next;
        _portable = portable;
    }

    public async Task InvokeAsync(HttpContext ctx, IAuthService auth, AkashaDbContext db, RateLimiter limiter)
    {
        var path = ctx.Request.Path.Value ?? "";

        // публичные пути
        if (!path.StartsWith("/api/v1") || path == "/healthz" ||
            path.StartsWith("/api/v1/auth/login"))
        {
            await _next(ctx);
            return;
        }

        Principal? principal = null;

        // 1) Bearer agent key
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            principal = await auth.AuthenticateAsync(authHeader, ctx.Connection.RemoteIpAddress?.ToString(), ctx.RequestAborted);
            if (principal is null)
            {
                await Fail(ctx, 401, "unauthorized", "invalid or revoked API key");
                return;
            }
        }
        // 2) owner session cookie
        else if (ctx.Request.Cookies.TryGetValue(ApiEndpoints.SessionCookie, out var token) &&
                 !string.IsNullOrEmpty(token) && SessionStore.TryValidate(token, out var email))
        {
            var owner = await db.Owners.AsNoTracking().FirstOrDefaultAsync(o => o.Email == email, ctx.RequestAborted);
            if (owner is not null) principal = Principal.Owner();
        }
        // 3) portable: localhost → owner
        if (principal is null && _portable && IsLocalhost(ctx))
            principal = Principal.Owner();

        if (principal is null)
        {
            await Fail(ctx, 401, "unauthorized", "no valid credentials");
            return;
        }

        // sync endpoints: разрешены всем аутентифицированным (агент с любым скоупом — это device)
        // admin: только owner
        if (path.StartsWith("/api/v1/admin") && !principal.IsOwner)
        {
            await Fail(ctx, 403, "forbidden", "admin requires owner session");
            return;
        }

        // scope check через метаданные endpoint'а (RequireScope)
        var required = ctx.GetEndpoint()?.Metadata.GetMetadata<RequiredScopeAttribute>();
        if (required is not null && !principal.HasScope(required.Scope))
        {
            await Fail(ctx, 403, "forbidden", $"scope '{required.Scope}' required");
            return;
        }

        // rate limit (§6.4): агенты — 60 req/min; owner не ограничивается; export пропускается
        if (!principal.IsOwner && !ctx.Items.ContainsKey("skip_rate_limit"))
        {
            if (!limiter.Allow(principal.Agent!.Id.ToString(), 60, TimeSpan.FromMinutes(1)))
            {
                ctx.Response.Headers.RetryAfter = "30";
                await Fail(ctx, 429, "rate_limited", "60 requests per minute exceeded");
                return;
            }
        }

        ctx.Items["principal"] = principal;
        await _next(ctx);
    }

    private static bool IsLocalhost(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is not null && (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(ctx.Connection.LocalIpAddress));
    }

    private static Task Fail(HttpContext ctx, int code, string error, string? detail = null)
    {
        ctx.Response.StatusCode = code;
        return ctx.Response.WriteAsJsonAsync(new ApiTypes.ErrorResponse(error, detail), ApiEndpoints.JsonOpts);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiredScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}

/// <summary>Простой sliding-window rate limiter (in-memory).</summary>
public class RateLimiter
{
    private readonly ConcurrentDictionary<string, LinkedList<DateTime>> _windows = new();

    public bool Allow(string key, int maxRequests, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var list = _windows.GetOrAdd(key, _ => new LinkedList<DateTime>());
        lock (list)
        {
            while (list.Count > 0 && now - list.First!.Value > window) list.RemoveFirst();
            if (list.Count >= maxRequests) return false;
            list.AddLast(now);
            return true;
        }
    }
}

public static class EndpointConventionBuilderExtensions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(new RequiredScopeAttribute(scope)));
        return builder;
    }

    public static TBuilder RequireOwner<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(OwnerMarker.Instance));
        return builder;
    }

    private sealed class OwnerMarker { public static readonly OwnerMarker Instance = new(); }
}

/// <summary>SQLite DbFactory для singleton-сервисов (AuditService, MetaService, AuthService).</summary>
public class DbContextFactory : IDbContextFactoryLike
{
    private readonly string _connectionString;
    private readonly bool _sqlite;

    public DbContextFactory(string connectionString, bool sqlite)
    {
        _connectionString = connectionString;
        _sqlite = sqlite;
    }

    public AkashaDbContext Create()
    {
        var opts = new DbContextOptionsBuilder<AkashaDbContext>();
        if (_sqlite) opts.UseSqlite(_connectionString);
        else opts.UseNpgsql(_connectionString);
        return new AkashaDbContext(opts.Options);
    }
}
