using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class PersonaInvitationStatusEndpoints
{
    public static void MapPersonaInvitationStatus(this WebApplication app, bool emailDeliveryEnabled)
    {
        app.MapGet("/local/v1/administration/persona-invitations",
            async (HttpContext http, TenantAccessClient access,
                IPersonaInvitationStatus owner, string? after) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                try
                {
                    var admin = await access.RequireAdministratorAsync(http);
                    var result = await owner.ListAsync(admin.TenantId, after, http.RequestAborted);
                    var checkedAdmin = await access.RequireAdministratorAsync(http);
                    if (checkedAdmin.TenantId != admin.TenantId || checkedAdmin.ActorId != admin.ActorId)
                        throw new TenantAccessException("ADMINISTRATION_CHANGED", 401);
                    return Results.Ok(new
                    {
                        result.TenantId,
                        result.Items,
                        result.NextCursor,
                        deliveryEnabled = emailDeliveryEnabled
                    });
                }
                catch (TenantBoundaryException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.Status);
                }
                catch (TenantAccessException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode);
                }
                catch (Exception failure) when (failure is System.Data.Common.DbException or HttpRequestException)
                {
                    return Results.Json(new { error = "INVITATION_STATUS_UNAVAILABLE" }, statusCode: 503);
                }
            });
        app.MapPost("/local/v1/administration/persona-invitations/{actorId}/cancel",
            async (string actorId, HttpContext http, TenantAccessClient access,
                IPersonaInvitationStatus owner) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                try
                {
                    // Recheck the same administrator session before the mutation.
                    var admin = await access.RequireAdministratorAsync(http);
                    await access.RequireAdministratorAsync(http);
                    var result = await owner.CancelAsync(admin.TenantId, actorId, http.RequestAborted);
                    return Results.Ok(result);
                }
                catch (TenantBoundaryException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.Status);
                }
                catch (TenantAccessException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode);
                }
                catch (Exception failure) when (failure is System.Data.Common.DbException or HttpRequestException)
                {
                    return Results.Json(new { error = "INVITATION_CANCEL_UNAVAILABLE" }, statusCode: 503);
                }
            });
        if (!emailDeliveryEnabled) return;
        app.MapPost("/local/v1/administration/persona-invitations/{actorId}/send",
            async (string actorId, HttpContext http, TenantAccessClient access,
                PersonaInvitationAdministration invitations) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                try
                {
                    var admin = await access.RequireAdministratorAsync(http);
                    var checkedAdmin = await access.RequireAdministratorAsync(http);
                    if (checkedAdmin.TenantId != admin.TenantId || checkedAdmin.ActorId != admin.ActorId)
                        throw new TenantAccessException("ADMINISTRATION_CHANGED", 401);
                    var result = await invitations.RequestDeliveryAsync(
                        admin.TenantId, actorId, http.RequestAborted);
                    return Results.Json(result, statusCode: StatusCodes.Status202Accepted);
                }
                catch (TenantBoundaryException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.Status);
                }
                catch (TenantAccessException failure)
                {
                    return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode);
                }
                catch (Exception failure) when (failure is System.Data.Common.DbException or HttpRequestException)
                {
                    return Results.Json(new { error = "INVITATION_DELIVERY_UNAVAILABLE" }, statusCode: 503);
                }
            });
    }
}
