using Monergy.Contracts;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence;

public static class EvidenceEndpoints
{
    public static IEndpointRouteBuilder MapEvidenceContracts(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (EvidenceStorageUnavailableException) { return Results.Json(new { error = "EVIDENCE_STORAGE_UNAVAILABLE" }, statusCode: 503); }
            catch (Monergy.Platform.TenantBoundaryException e) { return Results.Json(new { error = e.Code }, statusCode: e.Status); }
        });
        group.MapPost("/contracts/cid-020/v1", async (
            ContractRequest<CreateDocumentVersion> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.CreateDocumentVersionAsync(request, cancellationToken)));
        group.MapPost("/contracts/cid-021/v1", async (
            ContractRequest<GetEvidenceMetadata> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.GetEvidenceMetadataAsync(request, cancellationToken)));
        group.MapPost("/contracts/cid-022/v1", async (
            ContractRequest<GetEvidenceReference> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.GetEvidenceReferenceAsync(request, cancellationToken)));
        group.MapPost("/contracts/app-02/evidence-list/v1", async (
            ContractRequest<ListCustomerEvidence> request,
            EvidenceApplication application,
            CancellationToken cancellationToken) =>
            ToResult(await application.ListCustomerEvidenceAsync(request, cancellationToken)));
        return endpoints;
    }

    private static IResult ToResult<T>(ContractResult<T> result) => result.Outcome switch
    {
        ContractOutcome.Success => Results.Ok(result),
        ContractOutcome.Rejected => Results.Json(result, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
