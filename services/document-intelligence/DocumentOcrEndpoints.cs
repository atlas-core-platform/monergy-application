using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
namespace Monergy.Services.DocumentIntelligence;

public static class DocumentOcrEndpoints
{
    public static void MapDocumentOcr(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/customers/{customerId}/documents/{versionId}/ocr");
        group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapGet("", async (string customerId, string versionId, HttpContext http, TenantAccessClient access,
            TenantBoundaryOptions bindings, IEvidenceContentReader evidence, IDocumentOcrRepository repository) => {
            CustomerReviewHttp.NoQuery(http.Request);
            var (tenant, session) = TenantAccessClient.Headers(http);
            if (!TenantAccessClient.Identifier(customerId, 128) || !TenantAccessClient.Identifier(versionId, 160) || !bindings.AllowsCustomerLookup(customerId, tenant))
                throw new TenantBoundaryException("OCR_SOURCE_INVALID", 400);
            var before = await Authorize(http, customerId, access);
            var security = new TrustedSecurityContext(new(before.ActorId, "Customer", DateTimeOffset.UtcNow, session),
                new("document-intelligence", "local-uat-workspace:" + tenant), new("customer-advice", null, before.DecisionId, customerId));
            EvidenceContent source;
            try { source = await evidence.ReadAsync(versionId, customerId, security, "ocr-read-" + Guid.NewGuid().ToString("N"), http.RequestAborted) ?? throw new TenantBoundaryException("OCR_SOURCE_NOT_FOUND", 404); }
            catch (Exception e) when (e is EvidenceDependencyException or EvidenceContentUnsupportedException) { throw new TenantBoundaryException("OCR_SOURCE_UNAVAILABLE", 503); }
            if (source.Reference.CustomerId != customerId || source.Reference.DocumentVersionId != versionId) throw new TenantBoundaryException("OCR_SOURCE_INVALID", 503);
            DocumentOcrResult? result;
            try { result = await repository.ReadOcrAsync(tenant, customerId, versionId, http.RequestAborted); }
            catch (InvalidOperationException) { throw new TenantBoundaryException("OCR_STORAGE_UNAVAILABLE", 503); }
            if (result is not null && (!DocumentOcrRules.Valid(result) || result.TenantId != tenant || result.CustomerId != customerId ||
                result.DocumentVersionId != versionId || !string.Equals(result.SourceHash, source.Reference.ContentSha256, StringComparison.OrdinalIgnoreCase)))
                throw new TenantBoundaryException("OCR_RESULT_INVALID", 503);
            CustomerReviewHttp.Confirm(before, await Authorize(http, customerId, access));
            // Missing output is explicit. Reading never starts synchronous OCR work.
            return Results.Ok(result ?? new DocumentOcrResult(tenant, customerId, versionId, source.Reference.ContentSha256,
                DocumentOcrRules.Version, "NOT_PROCESSED", DateTimeOffset.UnixEpoch, []));
        });
    }
    private static async Task<CustomerAccessDecision> Authorize(HttpContext http, string customer, TenantAccessClient access)
    {
        var read = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer);
        var process = await access.EvaluateCustomerAsync(http, "document-intelligence.document.process", customer);
        if (read.Outcome != "Allow" || process.Outcome != "Allow" || read.AccessBasis != "Self" || process.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
        CustomerReviewHttp.Confirm(read, process); return read;
    }
}
