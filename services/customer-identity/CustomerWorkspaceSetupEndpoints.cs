using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity;

public static class CustomerWorkspaceSetupEndpoints
{
    public static void MapCustomerWorkspaceSetup(this WebApplication app, string contextToken)
    {
        app.MapGet("/local/v1/me/workspace-setup", async (HttpContext http, CustomerWorkspaceSetupApplication setup) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try
            {
                var (tenant, session) = TenantAccessClient.Headers(http);
                return Results.Ok(await setup.ReadAsync(new(tenant, session), http.RequestAborted).ConfigureAwait(false));
            }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
        });
        app.MapGet("/internal/v1/customer-workspace-setup/{actorId}", async (string actorId, HttpContext http, CustomerWorkspaceSetupApplication setup) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), contextToken))
                return Results.StatusCode(403);
            try { return Results.Ok(await setup.ReadForActorAsync(http.Request.Headers["X-Monergy-Tenant"].ToString(), actorId, http.RequestAborted).ConfigureAwait(false)); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
        });
    }
}
