using System.Collections.Concurrent;
using System.Security.Cryptography;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

internal sealed record PlatformSessionRequest(string Email, string Password);
internal sealed record PlatformActivationRequest(string Email);
internal sealed record PlatformActivationConfirmation(string Ticket, string Password);
internal sealed record PlatformPostureRequest(string OperationId, long ExpectedVersion, string Posture, string Note);
internal sealed record OrganizationCreateRequest(string RequestId, string OrganizationName, string DisplayName,
    string Country, string TimeZone, string InitialAdministratorEmail, string Placement);
internal sealed record IntegrationChangeRequest(string OperationId, long ExpectedRevision, bool Enabled, string Scope,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string>? Options, string? SecretValue, bool ClearSecret, string SubscriptionStatus);
internal sealed record GovernedProviderRuntimeAccess(string Token);
internal sealed record IntegrationProviderView(
    string Code, string DisplayName, string Category, bool Enabled, string Scope,
    string? Endpoint, string? ClientId, string? CallbackUri, string? Model,
    IReadOnlyDictionary<string, string> Options, bool SecretConfigured, DateTimeOffset? SecretUpdatedAt,
    string SubscriptionStatus, string Readiness, long Revision, string UpdatedBy, DateTimeOffset UpdatedAt);
internal sealed record IntegrationConfigurationView(long Revision, IReadOnlyList<IntegrationProviderView> Providers);

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
        var secretsPath = section["SecretsPath"] ?? Path.Combine(Path.GetDirectoryName(operationsPath) ?? string.Empty, "integration-secrets.json");
        var secretProtectionKey = section["SecretProtectionKey"];
        var runtimeToken = section["RuntimeToken"];
        var email = section["AdministratorEmail"] ?? "a950.platform@example.test";
        var testerCredential = section["TesterCredential"] ?? section["AccessKey"];
        if (new[] { operationsPath, credentialsPath, integrationsPath, secretsPath, secretProtectionKey,
                runtimeToken, email, testerCredential }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Platform Administration requires protected credential, operation and integration configuration.");
        services.AddSingleton(new FilePlatformCredentialStore(credentialsPath!, email!, testerCredential!));
        services.AddSingleton<PlatformAdministratorSessions>();
        services.AddSingleton(new FilePlatformOperationsStore(operationsPath!, Topology.Services.Select(item => item.Id)));
        services.AddSingleton(new FileIntegrationConfigurationStore(integrationsPath!));
        services.AddSingleton(new FileProtectedIntegrationSecretStore(secretsPath!, secretProtectionKey!));
        services.AddSingleton(new GovernedProviderRuntimeAccess(runtimeToken!));
        if (configuration.GetValue<bool>("Monergy:Onboarding:Enabled"))
        {
            var personaTesterCredential = section["PersonaTesterCredential"];
            var identityOwnerToken = section["IdentityOwnerToken"];
            if (string.IsNullOrWhiteSpace(personaTesterCredential) || string.IsNullOrWhiteSpace(identityOwnerToken))
                throw new InvalidOperationException("Platform Administration requires protected first-administrator activation.");
            services.AddSingleton(new LocalAdministratorWelcomeIssuer(
                new Uri(section["CustomerIdentityUrl"] ?? "http://127.0.0.1:5101/"), personaTesterCredential,
                identityOwnerToken));
        }
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
        if (app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled"))
        {
        app.MapGet("/platform-api/local/v1/organizations", async (HttpContext http,
            PlatformAdministratorSessions sessions, ITenantRegistry registry) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            var organizations = await registry.ListAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new
            {
                items = organizations.Select(tenant => new
                {
                    organizationCode = tenant.TenantId,
                    tenant.OrganizationName,
                    tenant.DisplayName,
                    country = tenant.CountryCode,
                    tenant.TimeZone,
                    initialAdministrator = tenant.InitialAdministratorEmail,
                    status = tenant.State.ToString(),
                    created = tenant.CreatedAt,
                    updated = tenant.UpdatedAt,
                    readiness = tenant.Receipts.Count == 6 ? "Provisioning complete" :
                        $"{tenant.Receipts.Count} of 6 setup stages complete",
                    attention = tenant.State == TenantLifecycleState.ProvisioningFailed ? tenant.LastFailureCode : null,
                    completedSteps = tenant.Receipts.Select(receipt => (int)receipt.Step).ToArray(),
                })
            });
        });
        app.MapPost("/platform-api/local/v1/organizations", async (OrganizationCreateRequest request,
            HttpContext http, PlatformAdministratorSessions sessions, TenantOnboardingOrchestrator orchestrator,
            FileTenantLifecycleAudit lifecycleAudit) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            if (request.Placement != "Shared")
                return Results.Json(new { error = "DEDICATED_PLACEMENT_UNAVAILABLE" }, statusCode: 422);
            try
            {
                var tenant = await orchestrator.RequestAsync(new(request.RequestId, request.OrganizationName,
                    request.DisplayName, request.Country, request.TimeZone, "LOCAL",
                    request.InitialAdministratorEmail, TenantPlacementMode.Shared), http.RequestAborted).ConfigureAwait(false);
                await lifecycleAudit.AppendAsync(tenant.TenantId + ":OrganizationRequested:1",
                    tenant.TenantId, "A950", "OrganizationRequested", "Succeeded", 1,
                    tenant.CreatedAt, http.RequestAborted).ConfigureAwait(false);
                tenant = await orchestrator.StartOrResumeAsync(tenant.TenantId, http.RequestAborted).ConfigureAwait(false);
                await AuditProvisioningAsync(tenant, lifecycleAudit, http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { organizationCode = tenant.TenantId, status = tenant.State.ToString(),
                    completedSteps = tenant.Receipts.Select(receipt => (int)receipt.Step).ToArray(), tenant.LastFailureCode });
            }
            catch (InvalidOperationException failure)
            {
                var conflict = failure.Message.Contains("CONFLICT", StringComparison.Ordinal);
                return Results.Json(new { error = failure.Message }, statusCode: conflict ? 409 : 422);
            }
        });
        app.MapPost("/platform-api/local/v1/organizations/{organizationCode}/administrator-welcome", async (
            string organizationCode, HttpContext http, PlatformAdministratorSessions sessions,
            ITenantRegistry registry, LocalAdministratorWelcomeIssuer welcome,
            FileTenantLifecycleAudit lifecycleAudit) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            var tenant = await registry.GetAsync(organizationCode, http.RequestAborted).ConfigureAwait(false);
            if (tenant is null) return Results.NotFound(new { error = "ORGANIZATION_NOT_FOUND" });
            if (tenant.State is not (TenantLifecycleState.ReadyForAdmin or TenantLifecycleState.Active) ||
                tenant.Receipts.Count != 6)
                return Results.Conflict(new { error = "ORGANIZATION_NOT_READY_FOR_ADMINISTRATOR" });
            var issued = await welcome.IssueAsync(tenant, http.RequestAborted).ConfigureAwait(false);
            await AuditInvitationAsync(tenant.TenantId, "A950", tenant.ConfigurationVersion,
                lifecycleAudit, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(issued);
        });
        app.MapPost("/platform-api/local/v1/organizations/{organizationCode}/resume", async (
            string organizationCode, HttpContext http, PlatformAdministratorSessions sessions,
            TenantOnboardingOrchestrator orchestrator, FileTenantLifecycleAudit lifecycleAudit) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            try
            {
                var tenant = await orchestrator.StartOrResumeAsync(organizationCode, http.RequestAborted).ConfigureAwait(false);
                await AuditProvisioningAsync(tenant, lifecycleAudit, http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { organizationCode = tenant.TenantId, status = tenant.State.ToString(),
                    completedSteps = tenant.Receipts.Select(receipt => (int)receipt.Step).ToArray(), tenant.LastFailureCode });
            }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: 409); }
        });
        app.MapPost("/uat-api/v1/administration/people/{actorId}/welcome", async (
            string actorId, HttpContext http, TenantAccessClient access,
            LocalAdministratorWelcomeIssuer welcome, FileTenantLifecycleAudit lifecycleAudit) =>
        {
            var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            if (!TenantAccessClient.Identifier(actorId, 128))
                return Results.BadRequest(new { error = "IDENTITY_INVALID" });
            var issued = await welcome.IssueAsync(
                administrator.TenantId, actorId, http.RequestAborted).ConfigureAwait(false);
            await AuditInvitationAsync(administrator.TenantId, administrator.ActorId,
                administrator.PolicyVersion, lifecycleAudit, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(issued);
        });
        }
        else
        {
            app.MapGet("/platform-api/local/v1/organizations", async (HttpContext http,
                PlatformAdministratorSessions sessions) =>
            {
                _ = await sessions.RequireAsync(http).ConfigureAwait(false);
                return Results.Ok(new { items = Array.Empty<object>() });
            });
        }
        app.MapGet("/platform-api/local/v1/audit", async (HttpContext http, PlatformAdministratorSessions sessions,
            FilePlatformOperationsStore store, FileIntegrationConfigurationStore integrations) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            var operations = await store.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            var integrationReceipts = await integrations.AuditAsync(http.RequestAborted).ConfigureAwait(false);
            var credentialReceipts = await http.RequestServices.GetRequiredService<FilePlatformCredentialStore>()
                .AuditAsync(http.RequestAborted).ConfigureAwait(false);
            var lifecycleItems = app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")
                ? await http.RequestServices.GetRequiredService<FileTenantLifecycleAudit>()
                    .ListAsync(http.RequestAborted).ConfigureAwait(false)
                : [];
            return Results.Ok(new { operationsRevision = operations.Version,
                items = operations.Receipts.Select(item => new { item.OperationId, item.ServiceId, posture = item.Posture.ToString(), item.Note, item.ActorId, item.RecordedAt, item.Version }),
                integrationItems = integrationReceipts, credentialItems = credentialReceipts, lifecycleItems });
        });
        app.MapGet("/platform-api/local/v1/integrations", async (HttpContext http, PlatformAdministratorSessions sessions,
            FileIntegrationConfigurationStore integrations, FileProtectedIntegrationSecretStore secrets,
            GovernedProviderRuntimeAccess runtime, HttpClient client) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            return Results.Ok(await SafeSnapshotAsync(integrations, secrets, runtime, client, http.RequestAborted).ConfigureAwait(false));
        });
        app.MapPut("/platform-api/local/v1/integrations/{code}", async (string code, IntegrationChangeRequest request,
            HttpContext http, PlatformAdministratorSessions sessions, FileIntegrationConfigurationStore integrations,
            FileProtectedIntegrationSecretStore secrets, GovernedProviderRuntimeAccess runtime, HttpClient client) =>
        {
            _ = await sessions.RequireAsync(http).ConfigureAwait(false);
            try
            {
                var before = await integrations.ReadAsync(http.RequestAborted).ConfigureAwait(false);
                var current = before.Providers.SingleOrDefault(item => item.Code == code);
                var tenant = request.Scope == "Organization" && request.Options?.TryGetValue("tenantId", out var value) == true
                    ? value : "platform";
                var reference = "provider:" + code + ":" + tenant;
                var currentReference = current?.SecretReferences.SingleOrDefault();
                var bindingChanged = currentReference is not null && currentReference != reference;
                if (bindingChanged && string.IsNullOrEmpty(request.SecretValue) && !request.ClearSecret)
                    throw new InvalidOperationException("INTEGRATION_SECRET_REQUIRED");
                IReadOnlyList<string> references = request.ClearSecret ? [] : !string.IsNullOrEmpty(request.SecretValue) ? [reference] :
                    currentReference is null ? [] : [currentReference];
                _ = await integrations.ChangeAsync(new(request.OperationId, request.ExpectedRevision, code,
                    request.Enabled, request.Scope, request.Endpoint, request.ClientId, request.CallbackUri, request.Model,
                    request.Options, references, request.SubscriptionStatus, "A950"), http.RequestAborted).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(request.SecretValue))
                    _ = await secrets.SetAsync(reference, request.SecretValue, "A950", http.RequestAborted).ConfigureAwait(false);
                else if (request.ClearSecret && currentReference is not null)
                    _ = await secrets.ClearAsync(currentReference, "A950", http.RequestAborted).ConfigureAwait(false);
                if (bindingChanged && currentReference is not null)
                    _ = await secrets.ClearAsync(currentReference, "A950", http.RequestAborted).ConfigureAwait(false);
                return Results.Ok(await SafeSnapshotAsync(integrations, secrets, runtime, client, http.RequestAborted).ConfigureAwait(false));
            }
            catch (InvalidOperationException failure)
            { return Results.Json(new { error = failure.Message }, statusCode: failure.Message.Contains("CONFLICT", StringComparison.Ordinal) ? 409 : 422); }
        });
        app.MapGet("/internal/local/v1/governed-integrations/{code}", async (string code, string tenant,
            HttpContext http, FileIntegrationConfigurationStore integrations, FileProtectedIntegrationSecretStore secrets,
            GovernedProviderRuntimeAccess runtime) =>
        {
            if (!AuthorizedRuntime(http, runtime.Token)) return Results.StatusCode(403);
            var snapshot = await integrations.ReadAsync(http.RequestAborted).ConfigureAwait(false);
            var provider = snapshot.Providers.SingleOrDefault(item => item.Code == code);
            if (provider is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            if (provider.Revision == 0) return Results.Ok(GovernedProviderConfigurationDecision.NeverAdopted());
            if (provider.Scope == "Organization" &&
                (!provider.Options.TryGetValue("tenantId", out var binding) || binding != tenant))
                return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                    GovernedProviderAdoptionStates.OrganizationMismatch));
            if (!provider.Enabled) return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                GovernedProviderAdoptionStates.Disabled));
            if (provider.Readiness == "Configuration incomplete")
                return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                    GovernedProviderAdoptionStates.ConfigurationIncomplete));
            if (provider.SecretReferences.Count != 1)
                return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                    GovernedProviderAdoptionStates.SecretUnavailable));
            string? secret;
            try { secret = await secrets.ResolveAsync(provider.SecretReferences[0], http.RequestAborted).ConfigureAwait(false); }
            catch (InvalidOperationException)
            { return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                GovernedProviderAdoptionStates.SecretUnavailable)); }
            if (secret is null) return Results.Ok(GovernedProviderConfigurationDecision.Unavailable(
                GovernedProviderAdoptionStates.SecretUnavailable));
            return Results.Ok(GovernedProviderConfigurationDecision.Ready(new GovernedProviderRuntimeConfiguration(provider.Code, provider.Scope,
                provider.Scope == "Organization" ? tenant : null, provider.Enabled, provider.Revision,
                provider.Endpoint, provider.ClientId, provider.CallbackUri, provider.Model, provider.Options, secret)));
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

    private static async Task<IntegrationConfigurationView> SafeSnapshotAsync(FileIntegrationConfigurationStore integrations,
        FileProtectedIntegrationSecretStore secrets, GovernedProviderRuntimeAccess runtime, HttpClient client, CancellationToken ct)
    {
        var snapshot = await integrations.ReadAsync(ct).ConfigureAwait(false);
        var views = new List<IntegrationProviderView>(snapshot.Providers.Count);
        foreach (var provider in snapshot.Providers)
        {
            var metadata = provider.SecretReferences.Count == 1
                ? await secrets.MetadataAsync(provider.SecretReferences[0], ct).ConfigureAwait(false)
                : new ProtectedSecretMetadata(string.Empty, false, null, null);
            var readiness = provider.Readiness;
            if (provider.Enabled && readiness == "Configuration complete — not yet tested")
            {
                var resolved = false;
                if (metadata.Configured)
                {
                    try { resolved = await secrets.ResolveAsync(provider.SecretReferences[0], ct).ConfigureAwait(false) is not null; }
                    catch (InvalidOperationException) { }
                }
                readiness = resolved
                    ? await OwnerReadinessAsync(provider, runtime.Token, client, ct).ConfigureAwait(false)
                    : "Secret missing/unresolved";
            }
            views.Add(new(provider.Code, provider.DisplayName, provider.Category, provider.Enabled, provider.Scope,
                provider.Endpoint, provider.ClientId, provider.CallbackUri, provider.Model, provider.Options,
                metadata.Configured, metadata.UpdatedAt, provider.SubscriptionStatus, readiness,
                provider.Revision, provider.UpdatedBy, provider.UpdatedAt));
        }
        return new(snapshot.Revision, views);
    }

    private static async Task<string> OwnerReadinessAsync(IntegrationProvider provider, string token, HttpClient client, CancellationToken ct)
    {
        var tenant = provider.Scope == "Organization" && provider.Options.TryGetValue("tenantId", out var binding)
            ? binding : "T001";
        var root = provider.Code == "email" ? "http://127.0.0.1:5101" : "http://127.0.0.1:5117";
        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/internal/local/v1/governed-providers/" +
            Uri.EscapeDataString(provider.Code) + "/readiness?tenant=" + Uri.EscapeDataString(tenant));
        request.Headers.Add("X-Monergy-Provider-Configuration", token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? "Ready" : "Needs attention";
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException)
        { return "Needs attention"; }
    }

    private static bool AuthorizedRuntime(HttpContext http, string expected)
    {
        var supplied = http.Request.Headers["X-Monergy-Provider-Configuration"].ToString();
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private static async Task AuditProvisioningAsync(TenantRegistryRecord tenant,
        FileTenantLifecycleAudit audit, CancellationToken ct)
    {
        await audit.AppendAsync(tenant.TenantId + ":ProvisioningStarted:2", tenant.TenantId,
            "A950", "ProvisioningStarted", "Succeeded", 2, tenant.CreatedAt, ct).ConfigureAwait(false);
        for (var index = 0; index < tenant.Receipts.Count; index++)
        {
            var receipt = tenant.Receipts[index];
            await audit.AppendAsync(receipt.OperationId + ":completed", tenant.TenantId,
                "platform-control-plane", "ProvisioningStepCompleted", receipt.Step.ToString(),
                index + 3, receipt.CompletedAt, ct).ConfigureAwait(false);
        }
        if (tenant.State == TenantLifecycleState.ProvisioningFailed)
            await audit.AppendAsync(tenant.TenantId + ":ProvisioningFailed:" + tenant.ConfigurationVersion,
                tenant.TenantId, "platform-control-plane", "ProvisioningFailed",
                tenant.LastFailureCode ?? "Failed", tenant.ConfigurationVersion, tenant.UpdatedAt, ct)
                .ConfigureAwait(false);
        if (tenant.State is TenantLifecycleState.ReadyForAdmin or TenantLifecycleState.Active &&
            tenant.Receipts.Count == 6)
            await audit.AppendAsync(tenant.TenantId + ":ReadyForAdministrator:9", tenant.TenantId,
                "platform-control-plane", "ReadyForAdministrator", "Succeeded", 9,
                tenant.Receipts[^1].CompletedAt, ct).ConfigureAwait(false);
    }

    private static Task AuditInvitationAsync(string tenantId, string actorId, long revision,
        FileTenantLifecycleAudit audit, CancellationToken ct) => audit.AppendAsync(
            tenantId + ":AdministratorInvitationIssued:" + Guid.NewGuid().ToString("N"),
            tenantId, actorId, "AdministratorInvitationIssued", "Succeeded", revision,
            DateTimeOffset.UtcNow, ct);

    private static object[] ReadinessGates() =>
    [
        new { id = "identity", state = "Blocked", detail = "Production identity provider, MFA and platform-administrator federation are not configured." },
        new { id = "transport", state = "Blocked", detail = "Workload identity, TLS and the production event transport require integration." },
        new { id = "operations", state = "Blocked", detail = "Managed secrets, backups, restore drills, monitoring, HA/DR and capacity acceptance remain required." },
        new { id = "business", state = "Blocked", detail = "Production OCR/AI providers, durable business persistence and financial-profile adoption remain unselected." },
        new { id = "governance", state = "Blocked", detail = "Local health and configuration metadata do not confer provider entitlement, legal approval or Production acceptance." },
    ];
}

internal sealed class LocalAdministratorWelcomeIssuer : IDisposable
{
    private readonly Uri owner;
    private readonly string testerCredential;
    private readonly string identityOwnerToken;
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(15) };

    public LocalAdministratorWelcomeIssuer(Uri owner, string testerCredential, string identityOwnerToken)
    {
        if (owner.Scheme != Uri.UriSchemeHttp || owner.Host is not ("127.0.0.1" or "localhost") ||
            string.IsNullOrWhiteSpace(testerCredential) || testerCredential.Length > 256 ||
            string.IsNullOrWhiteSpace(identityOwnerToken) || identityOwnerToken.Length > 256 ||
            testerCredential == identityOwnerToken)
            throw new InvalidOperationException("LOCAL_ADMINISTRATOR_WELCOME_CONFIGURATION_INVALID");
        this.owner = owner;
        this.testerCredential = testerCredential;
        this.identityOwnerToken = identityOwnerToken;
    }

    public async Task<object> IssueAsync(TenantRegistryRecord tenant, CancellationToken ct)
    {
        var identity = await ResolveIdentityAsync(tenant.TenantId, tenant.InitialAdministratorEmail, ct)
            .ConfigureAwait(false);
        return await IssueAsync(tenant.TenantId, identity.ActorId,
            tenant.OrganizationName, identity.NormalizedEmail, ct).ConfigureAwait(false);
    }

    public async Task<object> IssueAsync(string tenantId, string actorId, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(tenantId, 64) || !TenantAccessClient.Identifier(actorId, 128))
            throw new TenantBoundaryException("IDENTITY_INVALID", 400);
        using var identityRequest = new HttpRequestMessage(HttpMethod.Get,
            new Uri(owner, "internal/v1/tenant-identities/" + Uri.EscapeDataString(actorId)));
        identityRequest.Headers.Add("X-Monergy-Tenant", tenantId);
        identityRequest.Headers.Add("X-Monergy-Owner-Token", identityOwnerToken);
        using var identityResponse = await client.SendAsync(identityRequest, ct).ConfigureAwait(false);
        if (identityResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new TenantBoundaryException("IDENTITY_NOT_FOUND", 404);
        if (!identityResponse.IsSuccessStatusCode)
            throw new TenantBoundaryException("IDENTITY_OWNER_UNAVAILABLE", 503);
        var identity = await identityResponse.Content.ReadFromJsonAsync<ProvisionedIdentity>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new TenantBoundaryException("IDENTITY_OWNER_RESPONSE_INVALID", 503);
        if (identity.TenantId != tenantId || identity.ActorId != actorId ||
            string.IsNullOrWhiteSpace(identity.NormalizedEmail) || identity.NormalizedEmail.Length > 254)
            throw new TenantBoundaryException("IDENTITY_OWNER_RESPONSE_INVALID", 503);
        return await IssueAsync(tenantId, actorId, null, identity.NormalizedEmail, ct).ConfigureAwait(false);
    }

    private async Task<ProvisionedIdentity> ResolveIdentityAsync(string tenantId, string email, CancellationToken ct)
    {
        using var identityRequest = new HttpRequestMessage(HttpMethod.Post,
            new Uri(owner, "internal/v1/tenant-identities/resolve"));
        identityRequest.Headers.Add("X-Monergy-Tenant", tenantId);
        identityRequest.Headers.Add("X-Monergy-Owner-Token", identityOwnerToken);
        identityRequest.Content = JsonContent.Create(new { email });
        using var identityResponse = await client.SendAsync(identityRequest, ct).ConfigureAwait(false);
        if (identityResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new TenantBoundaryException("IDENTITY_NOT_FOUND", 404);
        if (!identityResponse.IsSuccessStatusCode)
            throw new TenantBoundaryException("IDENTITY_OWNER_UNAVAILABLE", 503);
        var identity = await identityResponse.Content.ReadFromJsonAsync<ProvisionedIdentity>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new TenantBoundaryException("IDENTITY_OWNER_RESPONSE_INVALID", 503);
        if (identity.TenantId != tenantId || !TenantAccessClient.Identifier(identity.ActorId, 128) ||
            !string.Equals(identity.NormalizedEmail, email, StringComparison.Ordinal))
            throw new TenantBoundaryException("IDENTITY_OWNER_RESPONSE_INVALID", 503);
        return identity;
    }

    private async Task<object> IssueAsync(string tenantId, string actorId, string? organizationName,
        string email, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(owner, "local/v1/persona-credentials/tester/welcome-link"));
        request.Headers.Add("X-Monergy-Tester-Activation", testerCredential);
        request.Content = JsonContent.Create(new
        {
            tenantId,
            email,
            returnPath = "/access",
            requestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
        });
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException("ADMINISTRATOR_WELCOME_UNAVAILABLE", response.StatusCode == System.Net.HttpStatusCode.Conflict ? 409 : 503);
        var receipt = await response.Content.ReadFromJsonAsync<WelcomeLinkReceipt>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new TenantBoundaryException("ADMINISTRATOR_WELCOME_INVALID", 503);
        if (!receipt.WelcomePath.StartsWith("/welcome#tenant=" + Uri.EscapeDataString(tenantId) + "&ticket=",
                StringComparison.Ordinal) || receipt.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new TenantBoundaryException("ADMINISTRATOR_WELCOME_INVALID", 503);
        return new { receipt.WelcomePath, receipt.ExpiresAt, organizationName, organizationCode = tenantId,
            email, actorId };
    }

    public void Dispose() => client.Dispose();

    private sealed record ProvisionedIdentity(string TenantId, string ActorId, string NormalizedEmail);
    private sealed record WelcomeLinkReceipt(string WelcomePath, DateTimeOffset ExpiresAt);
}
