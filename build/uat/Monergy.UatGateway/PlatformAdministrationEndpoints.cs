using System.Collections.Concurrent;
using System.Security.Cryptography;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

internal sealed record PlatformSessionRequest(string Email, string Password);
internal sealed record PlatformActivationRequest(string Email);
internal sealed record PlatformActivationConfirmation(string Ticket, string Password);
internal sealed record PlatformPostureRequest(string OperationId, long ExpectedVersion, string Posture, string Note);
internal sealed record IntegrationChangeRequest(string OperationId, long ExpectedRevision, bool Enabled, string Scope,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string>? Options, IReadOnlyList<string>? SecretReferences, string SubscriptionStatus);

internal sealed class PlatformAdministratorSessions(FilePlatformCredentialStore credentials, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public async Task<object> EstablishAsync(string email, string password, CancellationToken cancellationToken)
    {
        var authentication = await credentials.AuthenticateAsync(email, password, cancellationToken).ConfigureAwait(false);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = clock.GetUtcNow().AddHours(8);
        sessions[token] = new(expiresAt, authentication.CredentialVersion);
        return new { actorId = authentication.ActorId, authority = "PlatformAdministrator", session = token, expiresAt };
    }

    public async Task<string> RequireAsync(HttpContext http)
    {
        if (http.Request.Headers.ContainsKey("X-Monergy-Tenant") || http.Request.Headers.ContainsKey("X-Monergy-Session"))
            throw new TenantBoundaryException("PLATFORM_AUTHORITY_SEPARATION_REQUIRED", 403);
        var token = http.Request.Headers["X-Monergy-Platform-Session"].ToString();
        if (string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(token, out var session) || session.ExpiresAt <= clock.GetUtcNow())
            throw new TenantBoundaryException("PLATFORM_SESSION_REQUIRED", 401);
        var status = await credentials.StatusAsync(http.RequestAborted).ConfigureAwait(false);
        if (!status.Activated || status.CredentialVersion != session.CredentialVersion)
        {
            sessions.TryRemove(token, out _);
            throw new TenantBoundaryException("PLATFORM_SESSION_REVOKED", 401);
        }
        return token;
    }

    public async Task RevokeAsync(HttpContext http) => sessions.TryRemove(await RequireAsync(http).ConfigureAwait(false), out _);
    private sealed record Session(DateTimeOffset ExpiresAt, long CredentialVersion);
}

internal static class PlatformAdministrationEndpoints
{
    public static IServiceCollection AddPlatformAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetRequiredSection("Monergy:PlatformAdministration");
        var operationsPath = section["OperationsPath"];
        var credentialsPath = section["CredentialsPath"] ?? Path.Combine(Path.GetDirectoryName(operationsPath) ?? string.Empty, "platform-credentials.json");
        var integrationsPath = section["IntegrationsPath"] ?? Path.Combine(Path.GetDirectoryName(operationsPath) ?? string.Empty, "integration-configuration.json");
        var email = section["AdministratorEmail"] ?? "a950.platform@example.test";
        var testerCredential = section["TesterCredential"] ?? section["AccessKey"];
        if (new[] { operationsPath, credentialsPath, integrationsPath, email, testerCredential }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Platform Administration requires protected credential, operation and integration configuration.");
        services.AddSingleton(new FilePlatformCredentialStore(credentialsPath!, email!, testerCredential!));
        services.AddSingleton<PlatformAdministratorSessions>();
        services.AddSingleton(new FilePlatformOperationsStore(operationsPath!, Topology.Services.Select(item => item.Id)));
        services.AddSingleton(new FileIntegrationConfigurationStore(integrationsPath!));
        return services;
    }

    public static void MapPlatformAdministration(this WebApplication app)
    {
        app.MapGet("/platform-api/local/v1/credentials", async (FilePlatformCredentialStore credentials, CancellationToken ct) =>
        {
            var status = await credentials.StatusAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { enabled = true, activation = "tester-link", status.Activated });
        });
        app.MapPost("/platform-api/local/v1/credentials/tester/welcome-link", async (PlatformActivationRequest request,
            HttpContext http, FilePlatformCredentialStore credentials) =>
        {
            try
            {
                var issued = await credentials.IssueActivationAsync(request.Email,
                    http.Request.Headers["X-Monergy-Platform-Tester"].ToString(), http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { welcomePath = "/platform?ticket=" + Uri.EscapeDataString(issued.Ticket), issued.ExpiresAt });
            }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message == "PLATFORM_TESTER_AUTHORIZATION_REQUIRED" ? 403 : 404); }
        });
        app.MapPost("/platform-api/local/v1/credentials/activation/confirm", async (PlatformActivationConfirmation request,
            FilePlatformCredentialStore credentials, CancellationToken ct) =>
        {
            try
            {
                await credentials.ActivateAsync(request.Ticket, request.Password, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message == "PLATFORM_PASSWORD_INVALID" ? 422 : 401); }
        });
        app.MapPost("/platform-api/local/v1/sessions", async (PlatformSessionRequest request, HttpContext http,
            PlatformAdministratorSessions sessions) =>
        {
            try { return Results.Ok(await sessions.EstablishAsync(request.Email, request.Password, http.RequestAborted).ConfigureAwait(false)); }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message == "PLATFORM_CREDENTIAL_LOCKED" ? 423 : 401); }
        });
        app.MapDelete("/platform-api/local/v1/sessions/current", async (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            await sessions.RevokeAsync(http).ConfigureAwait(false);
            return Results.NoContent();
        });
        app.MapGet("/platform-api/local/v1/context", async (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            return Results.Ok(new { actorId = "A950", authority = "PlatformAdministrator", environment = "Local UAT",
                tenantAccess = false, customerDataAccess = false, breakGlassAvailable = false, productionAccepted = false });
        });
        app.MapGet("/platform-api/local/v1/topology", async (HttpContext http, PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store, HttpClient client) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            var health = await Topology.ReadAsync(client, http.RequestAborted,
                app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")).ConfigureAwait(false);
            var operations = await store.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { services = health, operationsRevision = operations.Version,
                postures = operations.Services.Select(item => new { item.ServiceId, posture = item.Posture.ToString(), item.Note, item.ChangedBy, item.ChangedAt, item.Version }) });
        });
        app.MapGet("/platform-api/local/v1/readiness", async (HttpContext http, PlatformAdministratorSessions sessions) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            return Results.Ok(new { productionAccepted = false, gates = ReadinessGates() });
        });
        app.MapGet("/platform-api/local/v1/audit", async (HttpContext http, PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store, FileIntegrationConfigurationStore integrations) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            var operations = await store.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            var integrationReceipts = await integrations.AuditAsync(http.RequestAborted).ConfigureAwait(false);
            var credentialReceipts = await http.RequestServices.GetRequiredService<FilePlatformCredentialStore>()
                .AuditAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { operationsRevision = operations.Version,
                items = operations.Receipts.Select(item => new { item.OperationId, item.ServiceId, posture = item.Posture.ToString(), item.Note, item.ActorId, item.RecordedAt, item.Version }),
                integrationItems = integrationReceipts, credentialItems = credentialReceipts });
        });
        app.MapGet("/platform-api/local/v1/integrations", async (HttpContext http, PlatformAdministratorSessions sessions,
            FileIntegrationConfigurationStore integrations) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            return Results.Ok(await integrations.ReadAsync(http.RequestAborted).ConfigureAwait(false));
        });
        app.MapPut("/platform-api/local/v1/integrations/{code}", async (string code, IntegrationChangeRequest request,
            HttpContext http, PlatformAdministratorSessions sessions, FileIntegrationConfigurationStore integrations) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            try
            {
                return Results.Ok(await integrations.ChangeAsync(new(request.OperationId, request.ExpectedRevision, code,
                    request.Enabled, request.Scope, request.Endpoint, request.ClientId, request.CallbackUri, request.Model,
                    request.Options, request.SecretReferences, request.SubscriptionStatus, "A950"), http.RequestAborted).ConfigureAwait(false));
            }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message.Contains("CONFLICT", StringComparison.Ordinal) ? 409 : 422); }
        });
        app.MapGet("/uat-api/v1/connected-services", async (HttpContext http, TenantAccessClient access,
            FileIntegrationConfigurationStore integrations) =>
        {
            var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var snapshot = await integrations.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new { snapshot.Revision, administrator.TenantId,
                providers = snapshot.Providers.Where(item => item.Enabled && item.Scope == "Organization" &&
                    item.Options.TryGetValue("tenantId", out var tenant) && tenant == administrator.TenantId)
                    .Select(item => new { item.Code, item.DisplayName, item.Category, item.Readiness, item.SubscriptionStatus, item.UpdatedAt }) });
        });
        app.MapPost("/platform-api/local/v1/services/{serviceId}/posture", async (string serviceId,
            PlatformPostureRequest request, HttpContext http, PlatformAdministratorSessions sessions, FilePlatformOperationsStore store) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            try
            {
                if (!Enum.TryParse<PlatformOperationalPosture>(request.Posture, false, out var posture) || !Enum.IsDefined(posture))
                    return Results.UnprocessableEntity(new { error = "PLATFORM_OPERATION_INVALID" });
                var result = await store.ChangeAsync(new PlatformPostureChange(request.OperationId, serviceId, posture,
                    request.Note, "A950", request.ExpectedVersion), http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (InvalidOperationException failure)
            {
                return failure.Message switch
                {
                    "PLATFORM_SERVICE_NOT_FOUND" => Results.NotFound(new { error = failure.Message }),
                    "PLATFORM_CONFIGURATION_CONFLICT" or "PLATFORM_OPERATION_REPLAY_CONFLICT" => Results.Conflict(new { error = failure.Message }),
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
        new { id = "governance", state = "Blocked", detail = "Local health and configuration metadata do not confer provider entitlement, legal approval or Production acceptance." },
    ];
}
