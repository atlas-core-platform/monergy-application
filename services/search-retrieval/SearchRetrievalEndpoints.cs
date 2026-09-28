using Monergy.Contracts;
using Monergy.Services.SearchRetrieval.Application;

namespace Monergy.Services.SearchRetrieval;

public static class SearchRetrievalEndpoints
{
    public static IEndpointRouteBuilder MapSearchRetrievalContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-042/v1", async (ContractRequest<SearchDocuments> request,
            SearchRetrievalApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.SearchDocumentsAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-043/v1", async (ContractRequest<SearchFinancialData> request,
            SearchRetrievalApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.SearchFinancialDataAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-044/v1", async (ContractRequest<BuildRetrievalContext> request,
            SearchRetrievalApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.BuildRetrievalContextAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-046/v1", async (ContractRequest<IndexRefreshRequested> request,
            SearchRetrievalApplication application, CancellationToken cancellationToken) =>
            ToResult(await application.RefreshAsync(request, cancellationToken)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        _ when result.Error?.Category == ContractErrorCategory.AuthenticationRequired => Results.Json(result, statusCode: 401),
        _ when result.Error?.Category == ContractErrorCategory.AccessDenied => Results.Json(result, statusCode: 403),
        _ => Results.Json(result, statusCode: 400),
    };
}
