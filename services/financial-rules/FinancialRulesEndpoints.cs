using Monergy.Contracts;
using Monergy.Services.FinancialRules.Application;

namespace Monergy.Services.FinancialRules;

public static class FinancialRulesEndpoints
{
    public static IEndpointRouteBuilder MapFinancialRulesContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-037/v1", async (ContractRequest<ExecuteCalculation> request,
            FinancialRulesApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.ExecuteAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-038/v1", async (ContractRequest<GetCalculationResult> request,
            FinancialRulesApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GetResultAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-039/v1", async (ContractRequest<ExplainCalculation> request,
            FinancialRulesApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.ExplainAsync(request, cancellationToken)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
