using Monergy.Services.ApplicationCase.Application;

namespace Monergy.Services.ApplicationCase;

public static class ApplicationCaseEndpoints
{
    public static void MapApplicationCases(this WebApplication app)
    {
        var routes = app.MapGroup("/local/v1/cases");
        routes.MapGet("/context", async (HttpContext http, ApplicationCaseAuthority authority) =>
        {
            var context = await authority.RequireDsaAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                authority = "DsaCaseManager",
                context.PolicyVersion,
                context.SubjectVersion,
            });
        });
        routes.MapGet("/me", async (HttpContext http, ApplicationCaseAuthority authority,
            AssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireCustomerAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                items = await owner.ListCustomerAsync(context, http.RequestAborted).ConfigureAwait(false),
            });
        });
        routes.MapPost("/me", async (CreateAssistanceCase request, HttpContext http,
            ApplicationCaseAuthority authority, AssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireCustomerAsync(http).ConfigureAwait(false);
            var result = await owner.CreateAsync(context, request,
                _ => authority.RequireCustomerAsync(http), http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(result);
        });
        routes.MapGet("/me/{caseId}/history", async (string caseId, HttpContext http,
            ApplicationCaseAuthority authority, AssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireCustomerAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.HistoryAsync(caseId, context, http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapGet("/work", async (HttpContext http, ApplicationCaseAuthority authority,
            AssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireDsaAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                items = await owner.ListDsaAsync(context, http.RequestAborted).ConfigureAwait(false),
            });
        });
        routes.MapPost("/{caseId}/accept", async (string caseId, AcceptAssistanceCase request,
            HttpContext http, ApplicationCaseAuthority authority, AssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireDsaAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.AcceptAsync(caseId, context, request,
                _ => authority.RequireDsaAsync(http), http.RequestAborted).ConfigureAwait(false));
        });
    }
}
