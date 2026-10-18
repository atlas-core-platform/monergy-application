using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity;

public sealed record ConsentRecipientLookup(string Email);
public interface IConsentRecipientDirectory
{
    Task<CustomerOwnerRecipientLabel?> ResolveAsync(string tenant, string email, CancellationToken ct);
}
public static class ConsentRecipientEndpoints
{
    public static void MapConsentRecipientLookup(this WebApplication app, string token)
    {
        app.MapPost("/internal/v1/consent-recipients/resolve", async (
            ConsentRecipientLookup input, HttpContext http, IConsentRecipientDirectory directory) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!TenantAccessIntegration.TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), token))
                return (IResult)Results.StatusCode(403);
            try
            {
                var tenants = http.Request.Headers["X-Monergy-Tenant"];
                if (tenants.Count != 1) throw new TenantAccessException("INVALID_TENANT", 400);
                var tenant = tenants.ToString(); TenantAccessProtocol.Identifier(tenant, 64);
                var email = CustomerEmailAccess.NormalizeEmail(input.Email);
                var recipient = await directory.ResolveAsync(tenant, email, http.RequestAborted).ConfigureAwait(false);
                if (recipient is null) return Results.NotFound();
                if (recipient.TenantId != tenant || recipient.RecipientLabel != email || !TenantAccessClient.Identifier(recipient.ActorId, 128))
                    throw new TenantAccessException("RECIPIENT_BINDING_INVALID", 503);
                return Results.Ok(recipient);
            }
            catch (TenantAccessException failure) { return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode); }
            catch (System.Data.Common.DbException) { return Results.StatusCode(503); }
        });
    }
}
