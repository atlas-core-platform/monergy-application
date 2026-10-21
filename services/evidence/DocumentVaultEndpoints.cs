using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;

namespace Monergy.Services.Evidence;

public sealed record VaultReplacement(string OperationId, string ExpectedVersionId, string OriginalFileName,
    string ContentType, string ContentReference, string ContentSha256);

public static class DocumentVaultEndpoints
{
    public static void MapDocumentVault(this WebApplication app)
    {
        var group = app.MapGroup("/local/v1/customers/{customerId}/document-vault");
        group.AddEndpointFilter(Filter);
        group.MapGet("", async (string customerId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentVaultRepository repo) =>
        {
            var before = await Authorize(http, customerId, access, bindings);
            var q = http.Request.Query;
            if (q.Any(x => x.Key is not ("category" or "search" or "after") || x.Value.Count != 1)) throw new TenantBoundaryException("VAULT_QUERY_INVALID", 400);
            var page = await repo.VaultListAsync(new(before.TenantId, customerId), q["category"].FirstOrDefault() ?? "all", q["search"].ToString(), q["after"].FirstOrDefault(), http.RequestAborted);
            Confirm(before, await Authorize(http, customerId, access, bindings));
            return page;
        });
        group.MapGet("/{versionId}", async (string customerId, string versionId, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentVaultRepository repo) =>
        {
            NoQuery(http); Id(versionId);
            var before = await Authorize(http, customerId, access, bindings);
            var row = await repo.VaultReadAsync(new(before.TenantId, customerId), versionId, http.RequestAborted) ?? throw new TenantBoundaryException("VAULT_NOT_FOUND", 404);
            Confirm(before, await Authorize(http, customerId, access, bindings));
            return row;
        });
        group.MapPut("/{versionId}/metadata", async (string customerId, string versionId, VaultMetadataChange change, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IDocumentVaultRepository repo) =>
        {
            NoQuery(http); Id(versionId); DocumentVault.Validate(change);
            var before = await Authorize(http, customerId, access, bindings, true);
            Confirm(before, await Authorize(http, customerId, access, bindings, true));
            return await repo.VaultSaveAsync(new(before.TenantId, customerId), versionId, before.ActorId, change, http.RequestAborted);
        });
        group.MapPost("/{documentId}/replacement", async (string customerId, string documentId, VaultReplacement change, HttpContext http, TenantAccessClient access, TenantBoundaryOptions bindings, IEvidenceRepository repo, EvidenceApplication owner) =>
        {
            NoQuery(http); Id(documentId); Id(change.ExpectedVersionId);
            if (!TenantAccessClient.Identifier(change.OperationId, 80) || change.OriginalFileName.Length > 255 || change.OriginalFileName.Any(char.IsControl)) throw new TenantBoundaryException("VAULT_REPLACEMENT_INVALID", 400);
            var before = await Authorize(http, customerId, access, bindings, true);
            var previous = await repo.GetVersionAsync(change.ExpectedVersionId, http.RequestAborted);
            if (previous is null || !new VaultScope(before.TenantId, customerId).Owns(previous) || previous.DocumentId != documentId) throw new TenantBoundaryException("VAULT_NOT_FOUND", 404);
            Confirm(before, await Authorize(http, customerId, access, bindings, true));
            var (_, session) = TenantAccessClient.Headers(http);
            var now = DateTimeOffset.UtcNow;
            var request = new ContractRequest<CreateDocumentVersion>(Vs02ContractNames.CreateDocumentVersion, ContractGuard.CurrentVersion,
                "vault-" + change.OperationId, "vault-" + change.OperationId, null,
                new(new(before.ActorId, "Customer", now, session), new("customer-web", "local-uat-workspace:" + before.TenantId), new("customer-advice", null, before.DecisionId, customerId)),
                "vault-" + change.OperationId,
                new(documentId, "document-version-" + change.OperationId, "evidence-" + change.OperationId, customerId, "Customer replacement", change.OriginalFileName, change.ContentType, change.ContentReference, change.ContentSha256, now));
            var result = await owner.CreateDocumentVersionAsync(request, change.ExpectedVersionId, http.RequestAborted);
            if (result.Outcome != ContractOutcome.Success || result.Data is null) throw new TenantBoundaryException("VAULT_REPLACEMENT_REJECTED", result.Error?.Category == ContractErrorCategory.Conflict ? 409 : 422);
            return result.Data;
        });
    }
    private static async Task<CustomerAccessDecision> Authorize(HttpContext http, string customer, TenantAccessClient access, TenantBoundaryOptions bindings, bool write = false)
    {
        var (tenant, _) = TenantAccessClient.Headers(http);
        if (!bindings.AllowsCustomerLookup(customer, tenant)) throw new TenantBoundaryException("CUSTOMER_TENANT_MISMATCH", 403);
        var read = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer);
        if (read.Outcome != "Allow" || read.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
        if (write)
        {
            var upload = await access.EvaluateCustomerAsync(http, "evidence.document.upload", customer);
            if (upload.Outcome != "Allow" || upload.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            Confirm(read, upload);
        }
        return read;
    }
    private static void Confirm(CustomerAccessDecision before, CustomerAccessDecision after)
    {
        if (before.ActorId != after.ActorId || before.TenantId != after.TenantId || before.CustomerId != after.CustomerId || before.PolicyVersion != after.PolicyVersion || before.SubjectVersion != after.SubjectVersion || before.CustomerVersion != after.CustomerVersion)
            throw new TenantBoundaryException("VAULT_CONTEXT_CHANGED", 409);
    }
    private static void Id(string value) { if (!TenantAccessClient.Identifier(value, 160)) throw new TenantBoundaryException("VAULT_ID_INVALID", 400); }
    private static void NoQuery(HttpContext http) { if (http.Request.Query.Count != 0) throw new TenantBoundaryException("VAULT_QUERY_INVALID", 400); }
    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext; http.Response.Headers.CacheControl = "no-store";
        if (http.Request.Headers.Origin.Count > 0 && http.Request.Headers.Origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}") return Results.StatusCode(403);
        try { return await next(context); }
        catch (TenantBoundaryException e) { return Results.Json(new { error = e.Code }, statusCode: e.Status); }
        catch (Exception e) when (e is HttpRequestException or System.Data.Common.DbException or InvalidOperationException) { return Results.Json(new { error = "VAULT_UNAVAILABLE" }, statusCode: 503); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(503); }
    }
}
