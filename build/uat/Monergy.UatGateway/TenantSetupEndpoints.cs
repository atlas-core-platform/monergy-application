using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

public sealed record AccessPackStatus(
    string? InstalledVersion,
    string? InstalledIdentity,
    DateTimeOffset? InstalledAt,
    long PolicyVersion,
    string? LatestOperationId,
    string? LatestPlanHash,
    long? FromPolicyVersion,
    long? ToPolicyVersion);

public static class TenantSetupEndpoints
{
    private const string RequiredPackVersion = "2.12";
    private const string RequiredPackIdentity = "monergy-default-access-pack/2.12";

    public static void AddTenantSetup(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")) return;
        builder.Services.AddSingleton<ITenantRegistry>(_ => new FileTenantRegistry(
            TenantAccessIntegration.Required(builder.Configuration, "Monergy:Onboarding:RegistryPath")));
        builder.Services.AddSingleton(_ => new FileTenantSetup(
            TenantAccessIntegration.Required(builder.Configuration, "Monergy:Onboarding:SetupPath")));
        builder.Services.AddSingleton(_ => new FileTenantLifecycleAudit(
            TenantAccessIntegration.Required(builder.Configuration, "Monergy:Onboarding:LifecycleAuditPath")));
        var provisioningUrl = TenantAccessIntegration.Required(builder.Configuration,
            "Monergy:TenantProvisioning:Url");
        var provisioningToken = TenantAccessIntegration.Required(builder.Configuration,
            "Monergy:TenantProvisioning:Token");
        builder.Services.AddSingleton(new TenantProvisioningOwnerClient(new Uri(provisioningUrl), provisioningToken));
        builder.Services.AddSingleton(provider => new TenantOnboardingOrchestrator(provider.GetRequiredService<ITenantRegistry>(),
            Enum.GetValues<TenantProvisioningStep>().Select(step => new ProtectedProvisioningOperation(
                step, provider.GetRequiredService<TenantProvisioningOwnerClient>())), TimeProvider.System,
            provider.GetRequiredService<FileTenantSetup>()));
    }

    public static void MapTenantSetup(this WebApplication app)
    {
        var enabled = app.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled");
        app.MapGet("/uat-api/v1/setup", (HttpContext http, TenantAccessClient access, HttpClient client) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            if (!enabled) return (object)new { enabled = false, admin.TenantId, customerRelationships = false };
            var registry = http.RequestServices.GetRequiredService<ITenantRegistry>();
            var tenant = await registry.GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("TENANT_ONBOARDING_UNAVAILABLE", 503);
            var receipt = await http.RequestServices.GetRequiredService<FileTenantSetup>().GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            var pack = await ReadPackStatusAsync(http, client).ConfigureAwait(false);
            return (object)new
            {
                enabled = true,
                admin.TenantId,
                admin.ActorId,
                admin.PolicyVersion,
                tenant.OrganizationName,
                tenant.CountryCode,
                tenant.TimeZone,
                tenant.InitialAdministratorEmail,
                state = tenant.State.ToString(),
                receiptReference = receipt?.ReceiptReference,
                customerRelationships = true,
                accessPack = new
                {
                    pack.InstalledVersion,
                    pack.InstalledIdentity,
                    pack.InstalledAt,
                    pack.PolicyVersion,
                    ready = IsRequiredPack(pack)
                }
            };
        }));
        if (!enabled) return;
        app.MapPost("/uat-api/v1/setup", (TenantSetupSubmission request, HttpContext http, TenantAccessClient access,
            HttpClient client, ITenantRegistry registry, FileTenantSetup setup, TenantOnboardingOrchestrator orchestrator,
            FileTenantLifecycleAudit lifecycleAudit) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var pack = await ReadPackStatusAsync(http, client).ConfigureAwait(false);
            if (!IsRequiredPack(pack) || pack.PolicyVersion != admin.PolicyVersion)
                throw new InvalidOperationException("TENANT_ACCESS_PACK_REQUIRED");
            var tenant = await registry.GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("TENANT_ONBOARDING_UNAVAILABLE", 503);
            var receipt = await setup.CompleteAsync(tenant, admin.ActorId, admin.PolicyVersion, request, TimeProvider.System, http.RequestAborted).ConfigureAwait(false);
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            await lifecycleAudit.AppendAsync(active.TenantId + ":OrganizationActivated:" + active.ConfigurationVersion,
                active.TenantId, admin.ActorId, "OrganizationActivated", "Succeeded",
                active.ConfigurationVersion, active.UpdatedAt, http.RequestAborted).ConfigureAwait(false);
            return new { receipt.ReceiptReference, state = active.State.ToString() };
        }));
        app.MapPost("/uat-api/v1/setup/activate", (HttpContext http, TenantAccessClient access, HttpClient client,
            TenantOnboardingOrchestrator orchestrator, FileTenantLifecycleAudit lifecycleAudit) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var pack = await ReadPackStatusAsync(http, client).ConfigureAwait(false);
            if (!IsRequiredPack(pack) || pack.PolicyVersion != admin.PolicyVersion)
                throw new InvalidOperationException("TENANT_ACCESS_PACK_REQUIRED");
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            await lifecycleAudit.AppendAsync(active.TenantId + ":OrganizationActivated:" + active.ConfigurationVersion,
                active.TenantId, admin.ActorId, "OrganizationActivated", "Succeeded",
                active.ConfigurationVersion, active.UpdatedAt, http.RequestAborted).ConfigureAwait(false);
            return new { active.SetupReceiptReference, state = active.State.ToString() };
        }));
    }

    private static bool IsRequiredPack(AccessPackStatus pack) =>
        string.Equals(pack.InstalledVersion, RequiredPackVersion, StringComparison.Ordinal) &&
        string.Equals(pack.InstalledIdentity, RequiredPackIdentity, StringComparison.Ordinal);

    private static async Task<AccessPackStatus> ReadPackStatusAsync(HttpContext http, HttpClient client)
    {
        var (tenant, session) = TenantAccessClient.Headers(http);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://127.0.0.1:5088/v1/administration/default-access-pack");
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Session", session);
        using var response = await client.SendAsync(request, http.RequestAborted).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(
                response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "TENANT_ADMIN_REQUIRED"
                    : "ACCESS_OWNER_UNAVAILABLE",
                response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 503);
        var pack = await response.Content.ReadFromJsonAsync<AccessPackStatus>(
            cancellationToken: http.RequestAborted).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        if (pack.PolicyVersion < 1)
            throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        return pack;
    }

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
        catch (InvalidOperationException failure) when (failure.Message.StartsWith("TENANT_", StringComparison.Ordinal))
        { return Results.Json(new { error = failure.Message }, statusCode: 409); }
        catch (Exception failure) when (failure is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { return Results.Json(new { error = "TENANT_SETUP_STORE_UNAVAILABLE" }, statusCode: 503); }
    }

    private sealed class ProtectedProvisioningOperation(TenantProvisioningStep step,
        TenantProvisioningOwnerClient owner) : ITenantProvisioningOperation
    {
        public TenantProvisioningStep ProvisioningStep => step;
        public Task<TenantProvisioningResult> ExecuteAsync(TenantRegistryRecord tenant, string operationId,
            CancellationToken cancellationToken) => owner.ExecuteAsync(tenant, step, operationId, cancellationToken);
    }
}

public sealed class TenantProvisioningOwnerClient : IDisposable
{
    private static readonly JsonSerializerOptions OwnerJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private readonly Uri owner;
    private readonly string token;
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(2) };

    public TenantProvisioningOwnerClient(Uri owner, string token)
    {
        if (owner.Scheme != Uri.UriSchemeHttp || owner.Host is not ("127.0.0.1" or "localhost") ||
            string.IsNullOrWhiteSpace(token) || token.Length > 256)
            throw new InvalidOperationException("TENANT_PROVISIONING_OWNER_CONFIGURATION_INVALID");
        this.owner = owner;
        this.token = token;
    }

    public async Task<TenantProvisioningResult> ExecuteAsync(TenantRegistryRecord tenant,
        TenantProvisioningStep step, string operationId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(owner,
            "internal/local/v1/tenants/" + Uri.EscapeDataString(tenant.TenantId) + "/steps/" + step));
        request.Headers.Add("X-Monergy-Tenant-Provisioning", token);
        request.Headers.Add("X-Monergy-Operation", operationId);
        request.Content = JsonContent.Create(tenant, options: OwnerJson);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return TenantProvisioningResult.Failure(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.BadRequest => "PROVISIONING_OWNER_REQUEST_INVALID",
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        "PROVISIONING_OWNER_AUTHORIZATION_FAILED",
                    System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed =>
                        "PROVISIONING_OWNER_ROUTE_INVALID",
                    System.Net.HttpStatusCode.UnprocessableEntity => "PROVISIONING_OWNER_STATE_INVALID",
                    System.Net.HttpStatusCode.BadGateway => "PROVISIONING_OWNER_DEPENDENCY_UNAVAILABLE",
                    System.Net.HttpStatusCode.ServiceUnavailable => "PROVISIONING_OWNER_DATABASE_UNAVAILABLE",
                    (System.Net.HttpStatusCode)507 => "PROVISIONING_OWNER_STORAGE_UNAVAILABLE",
                    _ when (int)response.StatusCode >= 500 => "PROVISIONING_OWNER_FAILED",
                    _ => "PROVISIONING_OWNER_REJECTED",
                });
            return await response.Content.ReadFromJsonAsync<TenantProvisioningResult>(cancellationToken: ct)
                .ConfigureAwait(false) ?? TenantProvisioningResult.Failure("PROVISIONING_OWNER_RESPONSE_INVALID");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return TenantProvisioningResult.Failure("PROVISIONING_OWNER_UNAVAILABLE"); }
    }

    public void Dispose() => client.Dispose();
}
