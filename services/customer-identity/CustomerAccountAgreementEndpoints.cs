using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class CustomerAccountAgreementEndpoints
{
    public static void AddCustomerAccountAgreements(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new AccountAgreementCatalog(config));
        services.AddSingleton<ICustomerAccountAgreementRepository, PostgresCustomerAccountAgreements>();
        services.AddSingleton<CustomerAccountAgreementApplication>();
    }
    public static void MapCustomerAccountAgreements(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/me/account-agreements");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            if (HttpMethods.IsPost(http.Request.Method) && (!http.Request.HasJsonContentType() || http.Request.ContentLength is > 2048))
                return Results.BadRequest();
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "AGREEMENTS_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", (HttpContext http, CustomerAccountAgreementApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.ReadAsync(new(tenant, session), http.RequestAborted);
        });
        group.MapPost("", (HttpContext http, AccountAgreementChange change, CustomerAccountAgreementApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.AcceptAsync(new(tenant, session), change, http.RequestAborted);
        });
    }
}
