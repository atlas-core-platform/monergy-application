using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;

namespace Monergy.Services.JobManagement;

public sealed record ScheduleDocumentProcessingJob(
    ContractRequest<ScheduleJob> Job,
    string ProcessingId,
    string EvidenceReferenceId);

public sealed record DocumentProcessingJobView(
    ScheduledJob Job,
    ProcessingStatus? Processing,
    IReadOnlyList<ValidatedSourceFact> Facts);

public sealed record ListDocumentProcessingJobsRequest(
    TrustedSecurityContext Security,
    string CustomerId,
    int Limit = 20);

public sealed record DocumentProcessingActivityView(
    IReadOnlyList<ScheduledJob> Items);

public static class DocumentProcessingJobEndpoints
{
    public static IEndpointRouteBuilder MapDocumentProcessingJobs(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var token = TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:ControlToken");
        TenantAccessIntegration.ValidateToken(token);
        endpoints.MapPost("/internal/local/v1/jobs/document-processing", async (
            HttpRequest http,
            ScheduleDocumentProcessingJob input,
            JobManagementApplication application,
            DocumentProcessingJobRegistry registry,
            CancellationToken cancellationToken) =>
        {
            Require(http, input.Job.Security, token);
            Validate(input);
            var intent = new DocumentProcessingJobIntent(
                input.Job.Payload.JobId,
                input.ProcessingId,
                input.Job.Payload.PayloadReference,
                input.EvidenceReferenceId,
                input.Job.Payload.CustomerId,
                input.Job.IdempotencyKey!,
                input.Job.Payload.RequestedAt);
            DocumentProcessingJobIntent registered;
            try { registered = registry.Register(intent); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "DOCUMENT_PROCESSING_JOB_CONFLICT" }); }
            var normalized = input.Job with
            {
                Payload = input.Job.Payload with { RequestedAt = registered.RequestedAt },
            };
            return ToResult(await application.ScheduleJobAsync(normalized, cancellationToken).ConfigureAwait(false));
        });
        endpoints.MapPost("/internal/local/v1/jobs/document-processing/list", async (
            HttpRequest http,
            ListDocumentProcessingJobsRequest request,
            IJobRepository repository,
            TenantAccessClient access,
            CancellationToken cancellationToken) =>
        {
            Require(http, request.Security, token);
            if (request.CustomerId != request.Security.Access.CustomerId ||
                request.Limit is < 1 or > 100)
                throw new TenantBoundaryException("JOB_ACTIVITY_REQUEST_INVALID", 400);

            var tenant = http.Headers["X-Monergy-Tenant"].ToString();
            var decision = await access.EvaluateCustomerAsync(
                tenant,
                request.Security.Actor.AuthenticationContextId,
                "job-management.job.read",
                request.CustomerId,
                cancellationToken).ConfigureAwait(false);
            if (decision.Outcome != "Allow" || decision.ActorId != request.Security.Actor.ActorId)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);

            var jobs = await repository.ListCustomerAsync(
                request.CustomerId,
                "document-processing",
                request.Limit,
                cancellationToken).ConfigureAwait(false);
            if (jobs.Any(job =>
                job.CustomerId != request.CustomerId ||
                job.JobName != "document-processing"))
                throw new TenantBoundaryException("JOB_ACTIVITY_RESPONSE_INVALID", 503);

            return Results.Ok(new DocumentProcessingActivityView(
                jobs.Select(job => job.ToStatus()).ToArray()));
        });

        endpoints.MapPost("/internal/local/v1/jobs/status", async (
            HttpRequest http,
            ContractRequest<GetJobStatus> request,
            JobManagementApplication application,
            IJobRepository repository,
            IJobAuthorizationPolicy authorization,
            CancellationToken cancellationToken) =>
        {
            Require(http, request.Security, token);
            var result = await application.GetJobStatusAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ContractOutcome.Success && result.Data is not null)
            {
                var job = await repository.GetAsync(result.Data.JobId, cancellationToken).ConfigureAwait(false);
                if (job is null) return Results.NotFound();
                var denied = await authorization.AuthorizeAsync(
                    request.Security, job, request.CorrelationId, cancellationToken).ConfigureAwait(false);
                if (denied is not null)
                    return Results.Json(new { error = denied }, statusCode: StatusCodes.Status403Forbidden);
                if (job.JobName == "document-processing")
                {
                    var target = http.HttpContext.RequestServices.GetRequiredService<HttpDocumentProcessingTarget>();
                    var processing = await target.GetStatusAsync(job, request.Security, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new DocumentProcessingJobView(
                        result.Data,
                        processing?.Status,
                        processing?.Facts ?? []));
                }
            }
            return ToResult(result);
        });
        return endpoints;
    }

    private static void Require(HttpRequest request, TrustedSecurityContext security, string token)
    {
        if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Job-Token"].ToString(), token))
            throw new TenantBoundaryException("JOB_WORKLOAD_DENIED", 403);
        var tenant = request.Headers["X-Monergy-Tenant"];
        if (tenant.Count != 1 || !TenantAccessClient.Identifier(tenant[0], 64))
            throw new TenantBoundaryException("TRUSTED_TENANT_CONTEXT_REQUIRED", 403);
        _ = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, tenant[0], "local-uat-workspace");
    }

    private static void Validate(ScheduleDocumentProcessingJob input)
    {
        var payload = input.Job.Payload;
        if (input.Job.ContractName != Vs02ContractNames.ScheduleJob ||
            payload.JobName != "document-processing" ||
            payload.OwnerService != "Document Intelligence Service" ||
            payload.PayloadReference.Length is < 1 or > 160 ||
            input.ProcessingId.Length is < 1 or > 160 ||
            input.EvidenceReferenceId.Length is < 1 or > 160 ||
            !TenantAccessClient.Identifier(payload.JobId, 160) ||
            !TenantAccessClient.Identifier(payload.PayloadReference, 160) ||
            !TenantAccessClient.Identifier(input.ProcessingId, 160) ||
            !TenantAccessClient.Identifier(input.EvidenceReferenceId, 160))
            throw new TenantBoundaryException("DOCUMENT_PROCESSING_JOB_INVALID", 400);
    }

    private static IResult ToResult(ContractResult<ScheduledJob> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: result.Error?.Category switch
        {
            ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
            ContractErrorCategory.NotFound => 404,
            ContractErrorCategory.Conflict => 409,
            _ => 400,
        }),
        _ => Results.Json(result, statusCode: 503),
    };
}
