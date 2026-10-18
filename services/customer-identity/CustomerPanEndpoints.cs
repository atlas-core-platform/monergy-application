using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;
public static class CustomerPanEndpoints
{
    public static void AddCustomerPan(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new CustomerPanProtection(config));
        services.AddSingleton<ICustomerPanRepository, PostgresCustomerPan>();
        services.AddSingleton<CustomerPanApplication>();
    }
    public static void MapCustomerPan(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/me/pan");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
            if (HttpMethods.IsPut(http.Request.Method) && (!http.Request.HasJsonContentType() || http.Request.ContentLength is > 2048)) return Results.BadRequest();
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.Json(new { error = "PAN_PROTECTION_UNAVAILABLE" }, statusCode: 503); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "PAN_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", (HttpContext http, CustomerPanApplication owner) => { var (tenant, session) = TenantAccessClient.Headers(http); return owner.ReadAsync(new(tenant, session), http.RequestAborted); });
        group.MapPut("", (HttpContext http, CustomerPanChange change, CustomerPanApplication owner) => { var (tenant, session) = TenantAccessClient.Headers(http); return owner.SaveAsync(new(tenant, session), change, http.RequestAborted); });
    }
}
