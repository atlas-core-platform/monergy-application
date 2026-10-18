using Monergy.Platform;

namespace Monergy.Services.Consent;

public static class CustomerSharingEndpoints
{
    public static void MapCustomerSharing(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/consents/customers/{customerId}");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
            if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.StatusCode(403);
            try { return await next(context).ConfigureAwait(false); }
            catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
            catch (System.Data.Common.DbException) { return Results.Json(new { error = "CONSENT_STORE_UNAVAILABLE" }, statusCode: 503); }
        });
        group.MapGet("/sharing-options", (string customerId, HttpContext http, CustomerSharingApplication owner) => owner.OptionsAsync(customerId, http));
        group.MapPost("/sharing", (string customerId, CustomerSharingChange change, HttpContext http, CustomerSharingApplication owner) => owner.GrantAsync(customerId, change, http));
        group.MapGet("/grants/{grantId}/sharing-options", (string customerId, string grantId, HttpContext http, CustomerSharingReplacementApplication owner) => owner.OptionsAsync(customerId, grantId, http));
        group.MapPost("/grants/{grantId}/replacement", (string customerId, string grantId, CustomerSharingReplacement change, HttpContext http, CustomerSharingReplacementApplication owner) => owner.ReplaceAsync(customerId, grantId, change, http));
    }
}
