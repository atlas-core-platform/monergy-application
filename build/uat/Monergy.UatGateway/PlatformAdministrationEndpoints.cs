using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

internal sealed record PlatformSessionRequest(string Key);
internal sealed record PlatformPostureRequest(
    string OperationId,
    long ExpectedVersion,
    string Posture,
    string Note);

internal sealed class PlatformAdministratorSessions(string accessKey, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(accessKey));
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new(StringComparer.Ordinal);

    public object Establish(string key)
    {
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            throw new TenantBoundaryException("PLATFORM_CREDENTIAL_INVALID", 401);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = clock.GetUtcNow().AddHours(8);
        sessions[token] = expiresAt;
        return new { actorId = "A950", authority = "PlatformAdministrator", session = token, expiresAt };
    }

    public string Require(HttpContext http)
    {
        if (http.Request.Headers.ContainsKey("X-Monergy-Tenant") ||
            http.Request.Headers.ContainsKey("X-Monergy-Session"))
            throw new TenantBoundaryException("PLATFORM_AUTHORITY_SEPARATION_REQUIRED", 403);
        var token = http.Request.Headers["X-Monergy-Platform-Session"].ToString();
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var expiry) || expiry <= clock.GetUtcNow())
            throw new TenantBoundaryException("PLATFORM_SESSION_REQUIRED", 401);
        return token;
    }

    public void Revoke(HttpContext http) => sessions.TryRemove(Require(http), out _);
}

internal static class PlatformAdministrationEndpoints
{
    public static IServiceCollection AddPlatformAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection("Monergy:PlatformAdministration");
        var key = section["AccessKey"];
        var path = section["OperationsPath"];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Platform Administration requires a protected access key and operations path.");
        services.AddSingleton(new PlatformAdministratorSessions(key));
        services.AddSingleton(new FilePlatformOperationsStore(path, Topology.Services.Select(item => item.Id)));
        return services;
    }

    public static void MapPlatformAdministration(this WebApplication app)
    {
        app.MapPost("/platform-api/local/v1/sessions", (PlatformSessionRequest request, PlatformAdministratorSessions sessions) =>
            Results.Ok(sessions.Establish(request.Key)));
        app.MapDelete("/platform-api/local/v1/sessions/current", (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            sessions.Revoke(http);
            return Results.NoContent();
        });
        app.MapGet("/platform-api/local/v1/context", (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            _ = sessions.Require(http);
            return Results.Ok(new
            {
                actorId = "A950",
                authority = "PlatformAdministrator",
                environment = "Local UAT",
                tenantAccess = false,
                customerDataAccess = false,
                breakGlassAvailable = false,
                productionAccepted = false,
            });
        });
        app.MapGet("/platform-api/local/v1/topology", async (
            HttpContext http,
            PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store,
            HttpClient client) =>
        {
            _ = sessions.Require(http);
            var health = await Topology.ReadAsync(client, http.RequestAborted,
                app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")).ConfigureAwait(false);
            var operations = await store.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new
            {
                services = health,
                operationsRevision = operations.Version,
                postures = operations.Services.Select(item => new
                {
                    item.ServiceId,
                    posture = item.Posture.ToString(),
                    item.Note,
                    item.ChangedBy,
                    item.ChangedAt,
                    item.Version,
                }),
            });
        });
        app.MapGet("/platform-api/local/v1/readiness", (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            _ = sessions.Require(http);
            return Results.Ok(new { productionAccepted = false, gates = ReadinessGates() });
        });
        app.MapGet("/platform-api/local/v1/audit", async (
            HttpContext http,
            PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store) =>
        {
            _ = sessions.Require(http);
            var operations = await store.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new
            {
                operationsRevision = operations.Version,
                items = operations.Receipts.Select(item => new
                {
                    item.OperationId,
                    item.ServiceId,
                    posture = item.Posture.ToString(),
                    item.Note,
                    item.ActorId,
                    item.RecordedAt,
                    item.Version,
                }),
            });
        });
        app.MapPost("/platform-api/local/v1/services/{serviceId}/posture", async (
            string serviceId,
            PlatformPostureRequest request,
            HttpContext http,
            PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store) =>
        {
            _ = sessions.Require(http);
            try
            {
                if (!Enum.TryParse<PlatformOperationalPosture>(request.Posture, false, out var posture) ||
                    !Enum.IsDefined(posture))
                    return Results.UnprocessableEntity(new { error = "PLATFORM_OPERATION_INVALID" });
                var result = await store.ChangeAsync(new PlatformPostureChange(
                    request.OperationId, serviceId, posture, request.Note, "A950", request.ExpectedVersion),
                    http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (InvalidOperationException failure)
            {
                return failure.Message switch
                {
                    "PLATFORM_SERVICE_NOT_FOUND" => Results.NotFound(new { error = failure.Message }),
                    "PLATFORM_CONFIGURATION_CONFLICT" or "PLATFORM_OPERATION_REPLAY_CONFLICT" =>
                        Results.Conflict(new { error = failure.Message }),
                    _ => Results.UnprocessableEntity(new { error = failure.Message }),
                };
            }
        });
    }

    private static object[] ReadinessGates() =>
    [
        new { id = "identity", state = "Blocked", detail = "Production identity provider, MFA and platform-administrator federation are not configured." },
        new { id = "transport", state = "Blocked", detail = "Workload identity, TLS and the production event transport require integration." },
        new { id = "operations", state = "Blocked", detail = "Managed secrets, backups, restore drills, monitoring, HA/DR and capacity acceptance remain required." },
        new { id = "business", state = "Blocked", detail = "Production OCR/AI providers, durable business persistence and financial-profile adoption remain unselected." },
        new { id = "governance", state = "Blocked", detail = "Local health and posture records do not confer Production approval. Exceptional tenant support remains unavailable until separately approved." },
    ];
}
