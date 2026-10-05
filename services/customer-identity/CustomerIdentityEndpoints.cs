using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity;

public static class CustomerIdentityEndpoints
{
    public static IEndpointRouteBuilder MapCustomerIdentityContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-001/v1", async (ContractRequest<GetCustomer> request,
            CustomerIdentityApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GetCustomerAsync(request, cancellationToken).ConfigureAwait(false)));
        endpoints.MapPost("/contracts/cid-002/v1", async (ContractRequest<GetTrustedActorContext> request,
            CustomerIdentityApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.GetTrustedActorContextAsync(request, cancellationToken).ConfigureAwait(false)));
        endpoints.MapPost("/contracts/cid-003/v1", async (ContractRequest<RegisterOrUpdateCustomer> request,
            CustomerIdentityApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.RegisterOrUpdateCustomerAsync(request, cancellationToken).ConfigureAwait(false)));
        endpoints.MapPost("/contracts/cid-004/v1", async (ContractRequest<RecordKycResult> request,
            CustomerIdentityApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.RecordKycResultAsync(request, cancellationToken).ConfigureAwait(false)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result)
    {
        if (result.Outcome == ContractOutcome.Success)
        {
            return Results.Ok(result);
        }

        var status = result.Error?.Category switch
        {
            ContractErrorCategory.AuthenticationRequired => StatusCodes.Status401Unauthorized,
            ContractErrorCategory.AccessDenied => StatusCodes.Status403Forbidden,
            ContractErrorCategory.NotFound => StatusCodes.Status404NotFound,
            ContractErrorCategory.Conflict or ContractErrorCategory.DuplicateRequest => StatusCodes.Status409Conflict,
            ContractErrorCategory.PreconditionFailed => StatusCodes.Status428PreconditionRequired,
            ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(result, ContractJson.Options, statusCode: status);
    }
}
