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
            IAssistanceCaseApplication owner) =>
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
            ApplicationCaseAuthority authority, IAssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireCustomerAsync(http).ConfigureAwait(false);
            var result = await owner.CreateAsync(context, request,
                _ => authority.RequireCustomerAsync(http), http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(result);
        });
        routes.MapGet("/me/{caseId}/history", async (string caseId, HttpContext http,
            ApplicationCaseAuthority authority, IAssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireCustomerAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.HistoryAsync(caseId, context, http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapGet("/work", async (HttpContext http, ApplicationCaseAuthority authority,
            IAssistanceCaseApplication owner) =>
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
            HttpContext http, ApplicationCaseAuthority authority, IAssistanceCaseApplication owner) =>
        {
            var context = await authority.RequireDsaAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.AcceptAsync(caseId, context, request,
                _ => authority.RequireDsaAsync(http), http.RequestAborted).ConfigureAwait(false));
        });

        routes.MapGet("/advisor/customers/{customerId}/engagement", async (string customerId,
            HttpContext http, ApplicationCaseAuthority authority, IAdvisorEngagementApplication owner) =>
        {
            var context = await authority.RequireAdvisorEngagementAsync(http, customerId).ConfigureAwait(false);
            return Results.Ok(await owner.ReadAsync(context, http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/advisor/customers/{customerId}/notes", async (string customerId,
            AddAdvisorNote request, HttpContext http, ApplicationCaseAuthority authority,
            IAdvisorEngagementApplication owner) =>
        {
            var context = await authority.RequireAdvisorEngagementAsync(http, customerId).ConfigureAwait(false);
            return Results.Ok(await owner.AddNoteAsync(context, request,
                _ => authority.RequireAdvisorEngagementAsync(http, customerId), http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/advisor/customers/{customerId}/communications", async (string customerId,
            RecordCustomerCommunication request, HttpContext http, ApplicationCaseAuthority authority,
            IAdvisorEngagementApplication owner) =>
        {
            var context = await authority.RequireAdvisorEngagementAsync(http, customerId).ConfigureAwait(false);
            return Results.Ok(await owner.RecordCommunicationAsync(context, request,
                _ => authority.RequireAdvisorEngagementAsync(http, customerId), http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/advisor/customers/{customerId}/application-stage", async (string customerId,
            AdvanceAdvisorApplication request, HttpContext http, ApplicationCaseAuthority authority,
            IAdvisorEngagementApplication owner) =>
        {
            var context = await authority.RequireAdvisorEngagementAsync(http, customerId).ConfigureAwait(false);
            return Results.Ok(await owner.AdvanceApplicationAsync(context, request,
                _ => authority.RequireAdvisorEngagementAsync(http, customerId), http.RequestAborted).ConfigureAwait(false));
        });

        routes.MapGet("/lender/context", async (HttpContext http, ApplicationCaseAuthority authority) =>
        {
            var context = await authority.RequireLenderAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                authority = "LenderProfessional",
                context.PolicyVersion,
                context.SubjectVersion,
            });
        });
        routes.MapGet("/lender/applications", async (HttpContext http,
            ApplicationCaseAuthority authority, ILenderApplication owner) =>
        {
            var context = await authority.RequireLenderAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                items = await owner.ListAsync(context, http.RequestAborted).ConfigureAwait(false),
            });
        });
        routes.MapPost("/lender/applications/{applicationId}/documents/{documentCode}/review", async (
            string applicationId, string documentCode, ReviewLenderDocument request, HttpContext http,
            ApplicationCaseAuthority authority, ILenderApplication owner) =>
        {
            var context = await authority.RequireLenderAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.ReviewDocumentAsync(applicationId, documentCode, context, request,
                _ => authority.RequireLenderAsync(http), http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/lender/applications/{applicationId}/decision", async (
            string applicationId, RecordLenderDecision request, HttpContext http,
            ApplicationCaseAuthority authority, ILenderApplication owner) =>
        {
            var context = await authority.RequireLenderAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.RecordDecisionAsync(applicationId, context, request,
                _ => authority.RequireLenderAsync(http), http.RequestAborted).ConfigureAwait(false));
        });

        routes.MapGet("/financial-professional/context", async (HttpContext http,
            ApplicationCaseAuthority authority) =>
        {
            var context = await authority.RequireFinancialProfessionalAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                authority = "FinancialProfessional",
                context.PolicyVersion,
                context.SubjectVersion,
            });
        });
        routes.MapGet("/financial-professional/reviews", async (HttpContext http,
            ApplicationCaseAuthority authority, IFinancialProfessionalReview owner) =>
        {
            var context = await authority.RequireFinancialProfessionalAsync(http).ConfigureAwait(false);
            return Results.Ok(new
            {
                context.TenantId,
                context.ActorId,
                items = await owner.ListAsync(context,
                    (customer, _) => authority.RequireFinancialProfessionalReviewAsync(http, customer),
                    http.RequestAborted).ConfigureAwait(false),
            });
        });
        routes.MapPost("/financial-professional/reviews/{reviewId}/sources/{sourceCode}/review", async (
            string reviewId, string sourceCode, ReviewFinancialSource request, HttpContext http,
            ApplicationCaseAuthority authority, IFinancialProfessionalReview owner) =>
        {
            var context = await authority.RequireFinancialProfessionalAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.ReviewSourceAsync(reviewId, sourceCode, context, request,
                (customer, _) => authority.RequireFinancialProfessionalReviewAsync(http, customer),
                http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/financial-professional/reviews/{reviewId}/notes", async (
            string reviewId, AddFinancialProfessionalNote request, HttpContext http,
            ApplicationCaseAuthority authority, IFinancialProfessionalReview owner) =>
        {
            var context = await authority.RequireFinancialProfessionalAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.AddNoteAsync(reviewId, context, request,
                (customer, _) => authority.RequireFinancialProfessionalReviewAsync(http, customer),
                http.RequestAborted).ConfigureAwait(false));
        });
        routes.MapPost("/financial-professional/reviews/{reviewId}/certification", async (
            string reviewId, RecordFinancialCertification request, HttpContext http,
            ApplicationCaseAuthority authority, IFinancialProfessionalReview owner) =>
        {
            var context = await authority.RequireFinancialProfessionalAsync(http).ConfigureAwait(false);
            return Results.Ok(await owner.CertifyAsync(reviewId, context, request,
                (customer, _) => authority.RequireFinancialProfessionalReviewAsync(http, customer),
                http.RequestAborted).ConfigureAwait(false));
        });
    }
}
