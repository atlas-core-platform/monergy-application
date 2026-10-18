using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class SelfSessionEndpoints
{
    public static void AddSelfSessions(this IServiceCollection services)
    {
        services.AddSingleton<ISelfSessionRepository, PostgresSelfSessions>();
        services.AddSingleton<SelfSessionsApplication>();
    }
    public static void MapSelfSessions(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/me/sessions");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "SESSIONS_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("", (HttpContext http, string? after, SelfSessionsApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.ListAsync(new(tenant, session), after, http.RequestAborted);
        });
        group.MapPost("/revoke", (HttpContext http, SelfSessionRevocation change, SelfSessionsApplication owner) =>
        {
            var (tenant, session) = TenantAccessClient.Headers(http);
            return owner.RevokeAsync(new(tenant, session), change, http.RequestAborted);
        });
    }
}
