using Monergy.Platform;
using Monergy.Platform.ControlPlane;

namespace Monergy.UatGateway;

public static class TenantSetupEndpoints
{
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
        app.MapGet("/uat-api/v1/setup", (HttpContext http, TenantAccessClient access) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            if (!enabled) return (object)new { enabled = false, admin.TenantId, customerRelationships = false };
            var registry = http.RequestServices.GetRequiredService<ITenantRegistry>();
            var tenant = await registry.GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("TENANT_ONBOARDING_UNAVAILABLE", 503);
            var receipt = await http.RequestServices.GetRequiredService<FileTenantSetup>().GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
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
                customerRelationships = true
            };
        }));
        if (!enabled) return;
        app.MapPost("/uat-api/v1/setup", (TenantSetupSubmission request, HttpContext http, TenantAccessClient access,
            ITenantRegistry registry, FileTenantSetup setup, TenantOnboardingOrchestrator orchestrator) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var tenant = await registry.GetAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("TENANT_ONBOARDING_UNAVAILABLE", 503);
            var receipt = await setup.CompleteAsync(tenant, admin.ActorId, admin.PolicyVersion, request, TimeProvider.System, http.RequestAborted).ConfigureAwait(false);
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            return new { receipt.ReceiptReference, state = active.State.ToString() };
        }));
        app.MapPost("/uat-api/v1/setup/activate", (HttpContext http, TenantAccessClient access,
            TenantOnboardingOrchestrator orchestrator) => InvokeAsync(async () =>
        {
            var admin = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
            var active = await orchestrator.ActivateAsync(admin.TenantId, http.RequestAborted).ConfigureAwait(false);
            return new { active.SetupReceiptReference, state = active.State.ToString() };
        }));
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
