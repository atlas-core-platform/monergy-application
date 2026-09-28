using Monergy.Contracts;
using Monergy.Services.Reporting.Application;

namespace Monergy.Services.Reporting;

public static class ReportingEndpoints
{
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

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        _ when result.Error?.Category == ContractErrorCategory.AuthenticationRequired => Results.Json(result, statusCode: 401),
        _ when result.Error?.Category == ContractErrorCategory.AccessDenied => Results.Json(result, statusCode: 403),
        _ when result.Error?.Category == ContractErrorCategory.NotFound => Results.Json(result, statusCode: 404),
        _ => Results.Json(result, statusCode: 400),
    };
}
