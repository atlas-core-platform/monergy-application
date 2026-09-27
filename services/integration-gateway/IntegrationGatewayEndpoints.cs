using Monergy.Contracts;
using Monergy.Services.IntegrationGateway.Application;

namespace Monergy.Services.IntegrationGateway;

public static class IntegrationGatewayEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationGatewayContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-015/v1", async (ContractRequest<ExecuteProviderRequest> request,
            IntegrationGatewayApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.ExecuteAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-016/v1", async (ContractRequest<GetProviderOperationStatus> request,
            IntegrationGatewayApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GetStatusAsync(request, cancellationToken)));
        endpoints.MapGet("/operations/connectors/health", (IntegrationGatewayApplication application) =>
            Results.Ok(application.GetConnectorHealth()));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
