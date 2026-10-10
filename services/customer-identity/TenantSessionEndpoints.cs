using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class TenantSessionEndpoints
{
    public static void AddTenantSessionAuthority(this IServiceCollection services, IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        services.AddSingleton<PostgresTenantSessions>();
        services.AddSingleton<ITenantSessionRepository>(provider => provider.GetRequiredService<PostgresTenantSessions>());
        services.AddSingleton<PostgresTenantIdentities>();
        services.AddSingleton<ITenantMembershipClient, HttpTenantMembershipClient>();
        services.AddSingleton<ReferenceTenantAuthenticator>();
        services.AddSingleton<TenantSessionAuthority>();
        if (configuration["Monergy:AccessIntegration:CustomerRelationships"] == "true")
        {
            services.AddSingleton<PostgresCustomers>();
            services.AddSingleton<ITenantCustomerResourceDirectory>(provider => provider.GetRequiredService<PostgresCustomers>());
        }
        else services.AddSingleton<ITenantCustomerResourceDirectory, ReferenceTenantCustomerResourceDirectory>();
        services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
    }

    public static void MapTenantSessionAuthority(this WebApplication app)
    {
        var token = TenantAccessIntegration.Required(app.Configuration, "Monergy:AccessIntegration:CustomerIdentityToken");
        var eventToken = TenantAccessIntegration.Required(app.Configuration, "Monergy:AccessIntegration:CustomerIdentityEventToken");
        TenantAccessIntegration.ValidateToken(token);
        TenantAccessIntegration.ValidateToken(eventToken);
        if (app.Configuration["Monergy:AccessIntegration:CustomerRelationships"] == "true")
        {
            var contextToken = TenantAccessIntegration.Required(app.Configuration, "Monergy:AccessIntegration:CustomerContextToken");
            TenantAccessIntegration.ValidateToken(contextToken);
            if (contextToken == token || contextToken == eventToken || contextToken == app.Configuration["Monergy:AccessIntegration:IdentityProvisioningToken"])
                throw new InvalidOperationException("Customer context requires a separate workload credential.");
            app.MapGet("/internal/v1/customer-context/{customerId}", async (string customerId, HttpContext http, PostgresCustomers customers) =>
            {
                if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), contextToken))
                    return (IResult)Results.StatusCode(403);
                try
                {
                    var customer = await customers.ReadAsync(http.Request.Headers["X-Monergy-Tenant"].ToString(), customerId, http.RequestAborted).ConfigureAwait(false);
                    return customer is null ? Results.NotFound() : Results.Ok(customer);
                }
                catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
                catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
                catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
            });
        }
        // Separate workload credential: session validators cannot create identities.
        if (app.Configuration["Monergy:AccessIntegration:IdentityProvisioningToken"] is { } provisioningToken)
        {
            TenantAccessIntegration.ValidateToken(provisioningToken);
            if (provisioningToken == token || provisioningToken == eventToken)
                throw new InvalidOperationException("Identity provisioning requires a separate workload credential.");
            var loseProvisioningReceipt = app.Configuration["Monergy:ExecutionZone"] == "CI_EPHEMERAL" &&
                app.Configuration["Monergy:AccessIntegration:VerificationLoseProvisioningReceiptOnce"] == "true" ? 1 : 0;
            app.MapPost("/internal/v1/tenant-identities/provision", (TenantIdentityProvisioningRequest request,
                HttpContext http, PostgresTenantIdentities identities) =>
                TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), provisioningToken)
                    ? InvokeAsync(async () =>
                    {
                        var receipt = await identities.ProvisionAsync(http.Request.Headers["X-Monergy-Tenant"].ToString(), request, http.RequestAborted).ConfigureAwait(false);
                        // CI-only fault after durable commit, before acknowledgement.
                        if (Interlocked.Exchange(ref loseProvisioningReceipt, 0) == 1) http.Abort();
                        return receipt;
                    })
                    : Task.FromResult<IResult>(Results.StatusCode(403)));
        }
        app.MapGet("/internal/health/ready", async (ITenantSessionRepository repository, CancellationToken ct) =>
            await repository.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok() : (IResult)Results.StatusCode(503));
        app.MapPost("/local/v1/tenant-sessions", (TenantSessionEstablishment request, HttpContext http, TenantSessionAuthority authority) =>
            InvokeAsync(() => authority.EstablishAsync(request.TenantId, http.Request.Headers["X-Monergy-Reference-Authentication"].ToString(), http.RequestAborted)));
        app.MapPost("/local/v1/tenant-sessions/revoke", (TenantSessionLookup request, HttpContext http, TenantSessionAuthority authority) =>
            InvokeAsync(() => authority.EndAsync(request, http.RequestAborted)));
        app.MapGet("/local/v1/administration/resource-directory/customers", async (
            HttpContext http, TenantAccessClient access, ITenantCustomerResourceDirectory directory) =>
        {
            try
            {
                var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
                return Results.Ok(new
                {
                    resourceType = "customer",
                    items = await directory.ListAsync(administrator.TenantId, http.RequestAborted).ConfigureAwait(false)
                });
            }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
        });
        app.MapGet("/local/v1/administration/sessions", async (HttpContext http, string? after,
            TenantAccessClient access, PostgresTenantSessions sessions) =>
        {
            try
            {
                var administrator = await access.RequireAdministratorAsync(http).ConfigureAwait(false);
                return await InvokeAsync(() => sessions.ListForAdministratorAsync(administrator.TenantId, after, http.RequestAborted)).ConfigureAwait(false);
            }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
        });
        app.MapPost("/internal/v1/tenant-sessions/validate", (TenantSessionLookup request, HttpContext http, TenantSessionAuthority authority) =>
            TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), token)
                ? InvokeAsync(() => authority.ValidateAsync(request, http.RequestAborted))
                : Task.FromResult<IResult>(Results.StatusCode(403)));
        app.MapPost("/reference/events/{destination}", (string destination, AccessManagementEvent message, HttpContext http, ITenantSessionRepository repository) =>
            TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Reference-Transport"].ToString(), eventToken)
                ? InvokeAsync(() => repository.ConsumeAsync(http.Request.Headers["X-Monergy-Reference-Tenant"].ToString(), destination, message, http.RequestAborted))
                : Task.FromResult<IResult>(Results.StatusCode(403)));
    }

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (TenantAccessException exception) { return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode); }
        catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
    }
}
