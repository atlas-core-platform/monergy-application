using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;

namespace Monergy.Services.Reporting;

public static class ReportingEndpoints
{
    public static IEndpointRouteBuilder MapD11LocalShutdown(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var token = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:ControllerToken");
        endpoints.MapPost("/operations/local/control/stop", (HttpRequest request,
            IHostApplicationLifetime lifetime) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Local-Control"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });
        return endpoints;
    }

    public static IEndpointRouteBuilder MapReportingContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-051/v1", async (ContractRequest<GenerateReport> request,
            ReportingApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GenerateAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-052/v1", async (ContractRequest<GetReport> request,
            ReportingApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GetAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-051/v2", async (ContractRequest<GenerateReport> request,
            ReportGenerationJobs jobs, CancellationToken cancellationToken) =>
            ToResult(await jobs.ScheduleAsync(request, cancellationToken)));
        return endpoints;
    }

    public static IEndpointRouteBuilder MapReportGenerationJobs(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var token = TenantAccessIntegration.Required(configuration, "Monergy:ReportGeneration:ControlToken");
        TenantAccessIntegration.ValidateToken(token);
        endpoints.MapPost("/internal/local/v1/report-generation/execute", async (
            ExecuteReportGenerationJob input,
            HttpContext http,
            TenantAccessClient access,
            ReportGenerationJobs jobs,
            CancellationToken cancellationToken) =>
        {
            if (!LocalControlToken.IsValid(http.Request.Headers["X-Monergy-Report-Token"].ToString(), token))
                throw new TenantBoundaryException("REPORT_JOB_WORKLOAD_DENIED", 403);
            var tenant = http.Request.Headers["X-Monergy-Tenant"].ToString();
            if (!TenantAccessClient.Identifier(tenant, 64) ||
                input.OperationId.Length != 32 ||
                input.OperationId.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)) ||
                input.JobId != ReportingIdentity.JobId(input.OperationId) ||
                input.CustomerId != input.Security.Access.CustomerId)
                throw new TenantBoundaryException("REPORT_JOB_REQUEST_INVALID", 400);
            _ = LocalTenantSecurityContext.RequireTenant(
                input.Security.Workload?.WorkloadIdentityId, tenant, "local-uat-workspace");
            var decision = await access.EvaluateCustomerAsync(
                tenant,
                input.Security.Actor.AuthenticationContextId,
                "reporting.report.generate",
                input.CustomerId,
                cancellationToken).ConfigureAwait(false);
            if (decision.Outcome != "Allow" || decision.ActorId != input.Security.Actor.ActorId)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            http.Features.Set(new TenantBoundaryProof(tenant, decision.ActorId,
                input.Security.Actor.AuthenticationContextId, input.CustomerId,
                ReportingContractNames.GenerateReport, "reporting.report.generate", decision.DecisionId));
            var result = await jobs.ExecuteAsync(input.OperationId, input.JobId, input.Security, cancellationToken)
                .ConfigureAwait(false);
            return ToResult(result);
        });
        return endpoints;
    }

    public static IEndpointRouteBuilder MapLocalReportingOperations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/operations/local/reporting/status", (IReportingDispatchStatus status) =>
            Results.Ok(status.Snapshot()));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        _ when result.Error?.Category == ContractErrorCategory.AuthenticationRequired => Results.Json(result, statusCode: 401),
        _ when result.Error?.Category == ContractErrorCategory.AccessDenied => Results.Json(result, statusCode: 403),
        _ when result.Error?.Category == ContractErrorCategory.NotFound => Results.Json(result, statusCode: 404),
        _ when result.Error?.Category == ContractErrorCategory.Conflict => Results.Json(result, statusCode: 409),
        _ when result.Error?.Category is ContractErrorCategory.DependencyFailure or
            ContractErrorCategory.TemporarilyUnavailable => Results.Json(result, statusCode: 503),
        _ => Results.Json(result, statusCode: 400),
    };
}

public sealed record ExecuteReportGenerationJob(
    string OperationId,
    string JobId,
    string CustomerId,
    TrustedSecurityContext Security);
