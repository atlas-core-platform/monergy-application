using Microsoft.AspNetCore.Mvc;
using Monergy.Platform;

namespace Monergy.Services.Consent;

public static class CustomerConsentEndpoints
{
    private static readonly string[] OwnerTokens = ["CustomerContextToken", "CustomerIdentityToken", "MembershipToken"];
    public static void MapCustomerConsent(this WebApplication app)
    {
        app.MapGet("/health/ready", async (PostgresCustomerConsent store, CancellationToken ct) =>
            await store.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok() : (IResult)Results.StatusCode(503));
        var token = TenantAccessIntegration.Required(app.Configuration, "Monergy:AccessIntegration:ConsentToken");
        TenantAccessIntegration.ValidateToken(token);
        if (OwnerTokens.Any(key => token == app.Configuration["Monergy:AccessIntegration:" + key]))
            throw new InvalidOperationException("Consent evaluation requires a separate workload credential.");
        app.MapGet("/internal/health/ready", async (PostgresCustomerConsent store, CancellationToken ct) =>
            await store.ReadyAsync(ct).ConfigureAwait(false) ? Results.Ok() : (IResult)Results.StatusCode(503));
        app.MapPost("/internal/v1/consent/evaluate", (ConsentEvaluation request, HttpContext http, PostgresCustomerConsent store, CustomerOwnerClient owners) =>
            InvokeAsync(async () =>
            {
                if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), token))
                    throw new TenantBoundaryException("WORKLOAD_AUTHORIZATION_REQUIRED", 403);
                var tenant = http.Request.Headers["X-Monergy-Tenant"];
                if (tenant.Count != 1 || tenant[0] != request.TenantId) throw new TenantBoundaryException("TENANT_MISMATCH", 403);
                var customer = await owners.CustomerAsync(request.TenantId, request.CustomerId, http.RequestAborted).ConfigureAwait(false);
                return await store.EvaluateAsync(request.TenantId, customer, request, http.RequestAborted).ConfigureAwait(false);
            }));
        app.MapGet("/local/v1/consents/customers/{customerId}", (string customerId, HttpContext http, PostgresCustomerConsent store, CustomerOwnerClient owners) =>
            InvokeAsync(async () =>
            {
                var (session, _) = await RequireOwnerAsync(customerId, http, owners).ConfigureAwait(false);
                return await store.ListAsync(session.TenantId, customerId, http.RequestAborted).ConfigureAwait(false);
            }));
        app.MapPost("/local/v1/consents/customers/{customerId}/grants", (string customerId, ConsentGrantWrite request, HttpContext http, PostgresCustomerConsent store, CustomerOwnerClient owners) =>
            InvokeAsync(async () =>
            {
                if (customerId != request.CustomerId) throw new TenantBoundaryException("CUSTOMER_MISMATCH", 400);
                var (session, customer) = await RequireOwnerAsync(customerId, http, owners).ConfigureAwait(false);
                await owners.RequireActiveMemberAsync(session.TenantId, request.ActorId, http.RequestAborted).ConfigureAwait(false);
                return await store.GrantAsync(session.TenantId, customer, session.ActorId, request, http.RequestAborted).ConfigureAwait(false);
            }));
        app.MapDelete("/local/v1/consents/customers/{customerId}/grants/{grantId}", (string customerId, string grantId,
            [FromBody] ConsentRevocation request, HttpContext http, PostgresCustomerConsent store, CustomerOwnerClient owners) =>
            InvokeAsync(async () =>
            {
                var (session, customer) = await RequireOwnerAsync(customerId, http, owners).ConfigureAwait(false);
                return await store.RevokeAsync(session.TenantId, customer, session.ActorId, grantId, request, http.RequestAborted).ConfigureAwait(false);
            }));
    }

    private static async Task<(CustomerOwnerSession, CustomerOwnerContext)> RequireOwnerAsync(string customer, HttpContext http, CustomerOwnerClient owners)
    {
        var session = await owners.SessionAsync(http).ConfigureAwait(false);
        var context = await owners.CustomerAsync(session.TenantId, customer, http.RequestAborted).ConfigureAwait(false);
        PostgresCustomerConsent.RequireOwner(session.TenantId, context, session.ActorId, customer);
        return (session, context);
    }

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (TenantBoundaryException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
        catch (System.Data.Common.DbException) { return Results.Json(new { error = "CONSENT_STORE_UNAVAILABLE" }, statusCode: 503); }
    }
}
