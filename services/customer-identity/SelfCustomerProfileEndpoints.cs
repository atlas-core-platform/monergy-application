using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class SelfCustomerProfileEndpoints
{
    public static void AddSelfCustomerProfiles(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISelfCustomerProfileRepository, PostgresSelfCustomerProfile>();
        services.AddSingleton<SelfCustomerProfileApplication>();
    }
    public static void MapSelfCustomerProfile(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/me/customer-profile");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "PROFILE_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", (HttpContext http, SelfCustomerProfileApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.ReadAsync(new(tenant, session), http.RequestAborted);
        });
        group.MapPut("", (HttpContext http, SelfCustomerProfileChange change, SelfCustomerProfileApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.SaveAsync(new(tenant, session), change, http.RequestAborted);
        });
    }
}
