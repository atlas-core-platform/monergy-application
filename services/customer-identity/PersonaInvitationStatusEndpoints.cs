using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class PersonaInvitationStatusEndpoints
{
    public static void MapPersonaInvitationStatus(this WebApplication app)
    {
        app.MapGet("/local/v1/administration/persona-invitations",
            async (HttpContext http, TenantAccessClient access,
                PostgresPersonaInvitationStatus owner, string? after) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                try
                {
                    var admin = await access.RequireAdministratorAsync(http);
                    var result = await owner.ListAsync(admin.TenantId, after, http.RequestAborted);
                    var checkedAdmin = await access.RequireAdministratorAsync(http);
                    if (checkedAdmin.TenantId != admin.TenantId || checkedAdmin.ActorId != admin.ActorId)
                        throw new TenantAccessException("ADMINISTRATION_CHANGED", 401);
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
                    return Results.Json(new { error = "INVITATION_STATUS_UNAVAILABLE" }, statusCode: 503);
                }
            });
    }
}
