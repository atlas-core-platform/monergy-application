using Monergy.Contracts;
using Monergy.Services.FinancialProfile.Application;

namespace Monergy.Services.FinancialProfile;

public static class FinancialProfileEndpoints
{
    public static IEndpointRouteBuilder MapFinancialProfileContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-031/v1", async (
            ContractRequest<NormalizeSourceFacts> request,
            FinancialProfileApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.NormalizeSourceFactsAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-033/v1", async (
            ContractRequest<GetFinancialProvenance> request,
            FinancialProfileApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.GetFinancialProvenanceAsync(request, cancellationToken)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
