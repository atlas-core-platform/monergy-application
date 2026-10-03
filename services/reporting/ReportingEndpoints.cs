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
        _ => Results.Json(result, statusCode: 400),
    };
}
