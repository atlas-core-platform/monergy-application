using Monergy.Contracts;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence;

public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceContracts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/contracts/cid-020/v1", async (
            ContractRequest<CreateDocumentVersion> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.CreateDocumentVersionAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-021/v1", async (
            ContractRequest<GetEvidenceMetadata> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.GetEvidenceMetadataAsync(request, cancellationToken)));
        endpoints.MapPost("/contracts/cid-022/v1", async (
            ContractRequest<GetEvidenceReference> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.GetEvidenceReferenceAsync(request, cancellationToken)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
