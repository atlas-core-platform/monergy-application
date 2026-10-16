using System.Net.Http.Json;
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
    private const string RequiredPackVersion = "2.2";
    private const string RequiredPackIdentity = "monergy-default-access-pack/2.2";

    public static void AddTenantSetup(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Onboarding:Enabled")) return;
        builder.Services.AddSingleton<ITenantRegistry>(_ => new FileTenantRegistry(
            TenantAccessIntegration.Required(builder.Configuration, "Monergy:Onboarding:RegistryPath")));
        builder.Services.AddSingleton(_ => new FileTenantSetup(
            TenantAccessIntegration.Required(builder.Configuration, "Monergy:Onboarding:SetupPath")));
        builder.Services.AddSingleton(provider => new TenantOnboardingOrchestrator(provider.GetRequiredService<ITenantRegistry>(),
            Enum.GetValues<TenantProvisioningStep>().Select(step => new ActivationOnlyOperation(step)), TimeProvider.System,
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
            HttpClient client, ITenantRegistry registry, FileTenantSetup setup, TenantOnboardingOrchestrator orchestrator) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var pack = await ReadPackStatusAsync(http, client).ConfigureAwait(false);
            if (!IsRequiredPack(pack) || pack.PolicyVersion != admin.PolicyVersion)
                throw new InvalidOperationException("TENANT_ACCESS_PACK_REQUIRED");
            var tenant = await registry.GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("TENANT_ONBOARDING_UNAVAILABLE", 503);
            var receipt = await setup.CompleteAsync(tenant, admin.ActorId, admin.PolicyVersion, request, TimeProvider.System, http.RequestAborted).ConfigureAwait(false);
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            return new { receipt.ReceiptReference, state = active.State.ToString() };
        }));
        app.MapPost("/uat-api/v1/setup/activate", (HttpContext http, TenantAccessClient access, HttpClient client,
            TenantOnboardingOrchestrator orchestrator) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var pack = await ReadPackStatusAsync(http, client).ConfigureAwait(false);
            if (!IsRequiredPack(pack) || pack.PolicyVersion != admin.PolicyVersion)
                throw new InvalidOperationException("TENANT_ACCESS_PACK_REQUIRED");
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
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

    // Provisioning remains in the owner bootstrap. The gateway can activate from
    // real persisted receipts, but cannot issue provisioning successes itself.
    private sealed class ActivationOnlyOperation(TenantProvisioningStep step) : ITenantProvisioningOperation
    {
        public TenantProvisioningStep ProvisioningStep => step;
        public Task<TenantProvisioningResult> ExecuteAsync(TenantRegistryRecord tenant, string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(TenantProvisioningResult.Failure("OWNER_BOOTSTRAP_REQUIRED"));
    }
}
