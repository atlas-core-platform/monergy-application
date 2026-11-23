using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Monergy.Platform;

namespace Monergy.UatGateway;

internal sealed partial class PlatformAdministratorSessions
{
    private readonly ConcurrentDictionary<string, BoundContext> assistantContexts = new(StringComparer.Ordinal);

    public async Task<PlatformPrincipalContext> RequireContextAsync(HttpContext http, string operation, string? resourceId)
    {
        var token = await RequireAsync(http).ConfigureAwait(false);
        if (!sessions.TryGetValue(token, out var session)) throw new TenantBoundaryException("PLATFORM_SESSION_REQUIRED", 401);
        var now = clock.GetUtcNow();
        foreach (var pair in assistantContexts.Where(pair => pair.Value.Context.ExpiresAt <= now)) assistantContexts.TryRemove(pair.Key, out _);
        if (assistantContexts.Count >= 4096) throw new TenantBoundaryException("PLATFORM_CONTEXT_CAPACITY", 503);
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiry = now.AddSeconds(30);
        if (expiry > session.ExpiresAt) expiry = session.ExpiresAt;
        var context = new PlatformPrincipalContext(session.PrincipalId, AssistantContract.Realm, session.CredentialVersion,
            expiry, reference, operation, resourceId);
        assistantContexts[reference] = new(token, context);
        return context;
    }

    public async Task<PlatformPrincipalContext> ValidateContextAsync(PlatformContextValidation request, CancellationToken ct)
    {
        if (!assistantContexts.TryGetValue(request.AuthenticationContext, out var bound) ||
            bound.Context.ExpiresAt <= clock.GetUtcNow() || bound.Context.Operation != request.Operation ||
            bound.Context.ResourceId != request.ResourceId || !sessions.TryGetValue(bound.SessionToken, out var session) ||
            session.ExpiresAt <= clock.GetUtcNow() || session.PrincipalId != bound.Context.PrincipalId)
            throw new TenantBoundaryException("PLATFORM_CONTEXT_INVALID", 401);
        var status = await credentials.PrincipalAsync(ct).ConfigureAwait(false);
        if (!status.Active || status.PrincipalId != bound.Context.PrincipalId || status.CredentialVersion != session.CredentialVersion)
            throw new TenantBoundaryException("PLATFORM_CONTEXT_REVOKED", 401);
        return bound.Context;
    }

    public async Task<PlatformPrincipalStatus> ValidatePrincipalAsync(string principal, CancellationToken ct)
    {
        var current = await credentials.PrincipalAsync(ct).ConfigureAwait(false);
        if (principal != current.PrincipalId) throw new TenantBoundaryException("PLATFORM_PRINCIPAL_UNAVAILABLE", 404);
        return new(current.PrincipalId, AssistantContract.Realm, current.CredentialVersion, current.Active);
    }

    private sealed record BoundContext(string SessionToken, PlatformPrincipalContext Context);
}

internal static class AssistantPlatformIdentity
{
    // Runs before browser-host routing. Only the internal loopback listener and a purpose-bound AM credential can enter.
    public static void UseAssistantPlatformIdentity(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments("/internal/platform-identity")) { await next(http).ConfigureAwait(false); return; }
            http.Response.Headers.CacheControl = "no-store";
            if (!string.Equals(app.Configuration["Monergy:Assistant:Enabled"], "true", StringComparison.OrdinalIgnoreCase) || http.Connection.RemoteIpAddress is not { } remote ||
                !IPAddress.IsLoopback(remote) || http.Request.Host.Host is not ("127.0.0.1" or "localhost") || http.Request.Method != "POST")
            { http.Response.StatusCode = 404; return; }
            try
            {
                AssistantContract.RequireWorkload(http, "X-Monergy-Platform-Identity", app.Configuration["Monergy:Assistant:IdentityValidationToken"]);
                var sessions = http.RequestServices.GetRequiredService<PlatformAdministratorSessions>();
                object value;
                if (http.Request.Path == "/internal/platform-identity/v1/validate")
                {
                    var request = await AssistantContract.ReadAsync<PlatformContextValidation>(http.Request, 2048, http.RequestAborted).ConfigureAwait(false);
                    value = await sessions.ValidateContextAsync(request, http.RequestAborted).ConfigureAwait(false);
                }
                else if (http.Request.Path == "/internal/platform-identity/v1/principals/validate")
                {
                    var request = await AssistantContract.ReadAsync<PlatformPrincipalValidation>(http.Request, 1024, http.RequestAborted).ConfigureAwait(false);
                    value = await sessions.ValidatePrincipalAsync(request.PrincipalId, http.RequestAborted).ConfigureAwait(false);
                }
                else { http.Response.StatusCode = 404; return; }
                await http.Response.WriteAsJsonAsync(value, AssistantContract.Json, http.RequestAborted).ConfigureAwait(false);
            }
            catch (TenantBoundaryException failure)
            { http.Response.StatusCode = failure.Status; await http.Response.WriteAsJsonAsync(new { error = failure.Code }, http.RequestAborted).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or InvalidOperationException)
            { http.Response.StatusCode = 503; await http.Response.WriteAsJsonAsync(new { error = "PLATFORM_IDENTITY_UNAVAILABLE" }, http.RequestAborted).ConfigureAwait(false); }
        });
    }
}
