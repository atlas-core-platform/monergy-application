using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement;

public static class ReportGenerationJobEndpoints
{
    public static IEndpointRouteBuilder MapReportGenerationJobs(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration,
        bool includeStatusEndpoint = false)
    {
        var token = TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:ControlToken");
        TenantAccessIntegration.ValidateToken(token);
        endpoints.MapPost("/internal/local/v1/jobs/report-generation", async (
            HttpRequest http,
            ContractRequest<ScheduleJob> request,
            JobManagementApplication application,
            CancellationToken cancellationToken) =>
        {
            Require(http, request.Security, token);
            var payload = request.Payload;
            if (request.ContractName != Vs02ContractNames.ScheduleJob ||
                payload.JobName != "report-generation" ||
                payload.OwnerService != ReportingIdentity.OwnerService ||
                payload.JobId != ReportingIdentity.JobId(payload.PayloadReference) ||
                payload.PayloadReference.Length != 32 ||
                payload.PayloadReference.Any(character =>
                    !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) ||
                payload.CustomerId != request.Security.Access.CustomerId)
                throw new TenantBoundaryException("REPORT_JOB_INVALID", 400);
            return ToResult(await application.ScheduleJobAsync(request, cancellationToken).ConfigureAwait(false));
        });
        if (includeStatusEndpoint)
        {
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
                }
                return ToResult(result);
            });
        }
        return endpoints;
    }

    private static void Require(HttpRequest request, TrustedSecurityContext security, string token)
    {
        if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Job-Token"].ToString(), token))
            throw new TenantBoundaryException("JOB_WORKLOAD_DENIED", 403);
        var tenant = request.Headers["X-Monergy-Tenant"].ToString();
        if (!TenantAccessClient.Identifier(tenant, 64))
            throw new TenantBoundaryException("TRUSTED_TENANT_CONTEXT_REQUIRED", 403);
        _ = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, tenant, "local-uat-workspace");
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
