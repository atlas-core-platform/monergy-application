using Microsoft.AspNetCore.Mvc;

namespace Monergy.AccessManagement;

public static class AdministrationEndpoints
{
    // Distinct financial-advisor entry; does not grant customer business access.
    public static void MapAdvisorWorkspace(this WebApplication app)
    {
        app.MapGet("/v1/advisor/context", (Func<HttpContext, Task<IResult>>)(http =>
            ExecuteAsync(http, (service, context, ct) => service.AdvisorContextAsync(context, ct))));
    }

    public static void MapCustomerRelationships(this WebApplication app)
    {
        var routes = app.MapGroup("/v1/administration/customer-relationships");
        routes.MapGet("/", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.CustomerRelationships, after, limit ?? 50, ct)));
        routes.MapPost("/", (CustomerRelationshipWrite request, ICustomerContextClient customers, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.AssignCustomerAsync(context, request, customers, ct)));
        routes.MapDelete("/{id}", (string id, [FromBody] PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.DeleteAsync(context, AdministrationEntity.CustomerRelationships, id, request.ExpectedPolicyVersion, ct)));
    }

    public static void MapAccessAdministration(this WebApplication app)
    {
        var routes = app.MapGroup("/v1/administration");
        routes.MapGet("/context", (Func<HttpContext, Task<IResult>>)(http =>
            ExecuteAsync(http, (service, context, ct) => service.ContextAsync(context, ct))));
        routes.MapPost("/members/{actorId}/revoke-sessions", (string actorId, PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.RevokeSessionsAsync(context, actorId, request, ct)));
        routes.MapGet("/groups", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.Groups, after, limit ?? 50, ct)));
        routes.MapPost("/groups", (GroupWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SaveGroupAsync(context, null, request, ct)));
        routes.MapPut("/groups/{id}", (string id, GroupWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SaveGroupAsync(context, id, request, ct)));
        routes.MapGet("/groups/{id}/members", (string id, HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListGroupMembersAsync(context, id, after, limit ?? 50, ct)));
        routes.MapPut("/groups/{id}/members", (string id, GroupMembersWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SetGroupMembersAsync(context, id, request, ct)));
        routes.MapDelete("/groups/{id}", (string id, [FromBody] PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.DeleteAsync(context, AdministrationEntity.Groups, id, request.ExpectedPolicyVersion, ct)));
        routes.MapGet("/imports", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.Imports, after, limit ?? 50, ct)));
        routes.MapPost("/imports/preview", (ImportPreviewRequest request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.PreviewImportAsync(context, request, ct)));
        routes.MapPost("/imports/{id}/commit", (string id, ImportCommitRequest request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.StageImportAsync(context, id, request, ct)));
        routes.MapGet("/imports/{id}", (string id, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.GetImportAsync(context, id, ct)));
        routes.MapPost("/imports/{id}/process", (string id, PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.ProcessImportAsync(context, id, request, ct)));
        routes.MapPost("/imports/{id}/cancel", (string id, PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.CancelImportAsync(context, id, request, ct)));
        routes.MapGet("/default-access-pack", (Func<HttpContext, Task<IResult>>)(http =>
            ExecuteAsync(http, (service, context, ct) => service.ReadDefaultAccessPackStatusAsync(context, ct))));
        routes.MapGet("/default-access-pack/plan", (Func<HttpContext, Task<IResult>>)(http =>
            ExecuteAsync(http, (service, context, ct) => service.PlanDefaultAccessPackAdoptionAsync(context, ct))));
        routes.MapPost("/default-access-pack/adopt", (DefaultAccessPackAdoptionRequest request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.AdoptDefaultAccessPackAsync(context, request, ct)));
        routes.MapGet("/permissions", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.Permissions, after, limit ?? 50, ct)));
        routes.MapGet("/roles", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.Roles, after, limit ?? 50, ct)));
        routes.MapGet("/members", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.Members, after, limit ?? 50, ct)));
        routes.MapGet("/resource-grants", (HttpContext http, string? after, int? limit) =>
            ExecuteAsync(http, (service, context, ct) => service.ListAsync(context, AdministrationEntity.ResourceGrants, after, limit ?? 50, ct)));
        routes.MapPost("/permissions", (PermissionWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SavePermissionAsync(context, null, request, ct)));
        routes.MapPut("/permissions/{id}", (string id, PermissionWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SavePermissionAsync(context, id, request, ct)));
        routes.MapDelete("/permissions/{id}", (string id, [FromBody] PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.DeleteAsync(context, AdministrationEntity.Permissions, id, request.ExpectedPolicyVersion, ct)));
        routes.MapPost("/roles", (RoleWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SaveRoleAsync(context, null, request, ct)));
        routes.MapPut("/roles/{id}", (string id, RoleWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.SaveRoleAsync(context, id, request, ct)));
        routes.MapDelete("/roles/{id}", (string id, [FromBody] PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.DeleteAsync(context, AdministrationEntity.Roles, id, request.ExpectedPolicyVersion, ct)));
        routes.MapPost("/members", (MemberCreate request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.AddMemberAsync(context, request, ct)));
        routes.MapPut("/members/{actorId}", (string actorId, MemberWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.UpdateMemberAsync(context, actorId, request, ct)));
        routes.MapPost("/resource-grants", (ResourceGrantWrite request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.AddResourceGrantAsync(context, request, ct)));
        routes.MapDelete("/resource-grants/{id}", (string id, [FromBody] PolicyRevision request, HttpContext http) =>
            ExecuteAsync(http, (service, context, ct) => service.DeleteAsync(context, AdministrationEntity.ResourceGrants, id, request.ExpectedPolicyVersion, ct)));
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext http, Func<PostgresAccessAdministration, TrustedActorContext, CancellationToken, Task<T>> operation)
    {
        var resolver = http.RequestServices.GetRequiredService<ITrustedSecurityContextResolver>();
        var trusted = await resolver.ResolveAsync(http).ConfigureAwait(false);
        if (trusted.Context is null)
            return Results.Json(new { error = trusted.Failure }, statusCode: trusted.Available ? 401 : 503);
        var service = http.RequestServices.GetService<PostgresAccessAdministration>();
        if (service is null)
            return Results.Json(new { error = "ADMINISTRATION_UNAVAILABLE" }, statusCode: 503);
        try
        {
            return Results.Ok(await operation(service, trusted.Context, http.RequestAborted).ConfigureAwait(false));
        }
        catch (AccessAdministrationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (AccessStoreUnavailableException)
        {
            return Results.Json(new { error = "TENANT_ACCESS_STORE_UNAVAILABLE" }, statusCode: 503);
        }
    }
}
