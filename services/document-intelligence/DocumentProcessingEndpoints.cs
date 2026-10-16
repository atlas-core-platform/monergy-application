using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence;

public sealed record DocumentProcessingView(
    ProcessingStatus Status,
    IReadOnlyList<ValidatedSourceFact> Facts);

public sealed record ReadDocumentProcessingResult(
    TrustedSecurityContext Security,
    string ProcessingId,
    string DocumentVersionId,
    string CustomerId);

public sealed record DocumentProcessingReadOnlyView(
    string ProcessingId,
    string DocumentVersionId,
    ProcessingState State,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ValidatedSourceFact> Facts);

public static class DocumentProcessingEndpoints
{
    public static IEndpointRouteBuilder MapDocumentProcessing(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var token = TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:ControlToken");
        TenantAccessIntegration.ValidateToken(token);
        endpoints.MapPost("/internal/local/v1/document-processing/execute", async (
            HttpRequest httpRequest,
            ContractRequest<ProcessDocument> request,
            DocumentProcessingApplication application,
            IDocumentProcessingRepository repository,
            CancellationToken cancellationToken) =>
        {
            Require(httpRequest, request.Security, token);
            var result = await application.ProcessDocumentAsync(request, cancellationToken).ConfigureAwait(false);
            return await ToViewAsync(result, repository, cancellationToken).ConfigureAwait(false);
        });
        endpoints.MapPost("/internal/local/v1/document-processing/read", async (
            HttpRequest httpRequest,
            ReadDocumentProcessingResult request,
            IDocumentProcessingRepository repository,
            CancellationToken cancellationToken) =>
        {
            Require(httpRequest, request.Security, token);
            if (!TenantAccessClient.Identifier(request.ProcessingId, 160) ||
                !TenantAccessClient.Identifier(request.DocumentVersionId, 160) ||
                !TenantAccessClient.Identifier(request.CustomerId, 160) ||
                request.CustomerId != request.Security.Access.CustomerId)
                throw new TenantBoundaryException("PROCESSING_READ_REQUEST_INVALID", 400);

            var record = await repository.GetAsync(request.ProcessingId, cancellationToken).ConfigureAwait(false);
            if (record is null ||
                record.CustomerId != request.CustomerId ||
                record.DocumentVersionId != request.DocumentVersionId)
                throw new TenantBoundaryException("PROCESSING_RESULT_NOT_FOUND", 404);

            IReadOnlyList<ValidatedSourceFact> facts = record.State == ProcessingState.Completed
                ? record.Facts
                : [];
            if (facts.Any(fact =>
                fact.CustomerId != record.CustomerId ||
                fact.DocumentVersionId != record.DocumentVersionId))
                throw new TenantBoundaryException("PROCESSING_RESULT_INVALID", 503);

            return Results.Ok(new DocumentProcessingReadOnlyView(
                record.ProcessingId,
                record.DocumentVersionId,
                record.State,
                record.UpdatedAt,
                facts));
        });

        endpoints.MapPost("/internal/local/v1/document-processing/status", async (
            HttpRequest httpRequest,
            ContractRequest<GetProcessingStatus> request,
            DocumentProcessingApplication application,
            IDocumentProcessingRepository repository,
            CancellationToken cancellationToken) =>
        {
            Require(httpRequest, request.Security, token);
            var result = await application.GetProcessingStatusAsync(request, cancellationToken).ConfigureAwait(false);
            return await ToViewAsync(result, repository, cancellationToken).ConfigureAwait(false);
        });
        return endpoints;
    }

    private static void Require(HttpRequest request, TrustedSecurityContext security, string token)
    {
        if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Document-Token"].ToString(), token))
            throw new TenantBoundaryException("DOCUMENT_PROCESSING_WORKLOAD_DENIED", 403);
        var tenant = request.Headers["X-Monergy-Tenant"];
        if (tenant.Count != 1 || !TenantAccessClient.Identifier(tenant[0], 64))
            throw new TenantBoundaryException("TRUSTED_TENANT_CONTEXT_REQUIRED", 403);
        _ = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, tenant[0], "local-uat-workspace", "job-management");
    }

    private static async Task<IResult> ToViewAsync(
        ContractResult<ProcessingStatus> result,
        IDocumentProcessingRepository repository,
        CancellationToken cancellationToken)
    {
        if (result.Data is null)
            return Results.Json(result, statusCode: result.Error?.Category switch
            {
                ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
                ContractErrorCategory.NotFound => 404,
                ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => 503,
                _ => 400,
            });
        var record = await repository.GetAsync(result.Data.ProcessingId, cancellationToken).ConfigureAwait(false);
        if (record is null) return Results.NotFound();
        return Results.Ok(new DocumentProcessingView(result.Data, record.Facts));
    }
}
