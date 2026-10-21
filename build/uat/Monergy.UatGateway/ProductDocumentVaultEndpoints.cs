using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductVaultMetadataChange(string OperationId, int ExpectedRevision, string Category, string? DocumentType, string? DocumentDate, string? FinancialYear);
public sealed record ProductVaultReplacement(string OperationId, string ExpectedVersionId, string OriginalFileName, string ContentType, string ContentBase64);

public static class ProductDocumentVaultEndpoints
{
    public static void MapProductDocumentVault(this WebApplication app)
    {
        var group = app.MapGroup("/product-api/v1/document-vault");
        group.MapGet("", async (HttpContext http, CustomerOwnerClient owners, ProductEvidenceClient evidence) =>
        {
            var (tenant, session, customer) = await Scope(http, owners);
            if (http.Request.Query.Any(x => x.Key is not ("category" or "search" or "after") || x.Value.Count != 1)) throw new TenantBoundaryException("VAULT_QUERY_INVALID", 400);
            return await evidence.VaultAsync(tenant, session, customer, "" + http.Request.QueryString, HttpMethod.Get, null, http.RequestAborted);
        });
        group.MapGet("/{versionId}", async (string versionId, HttpContext http, CustomerOwnerClient owners, ProductEvidenceClient evidence) =>
        {
            Id(versionId); NoQuery(http);
            var (tenant, session, customer) = await Scope(http, owners);
            return await evidence.VaultAsync(tenant, session, customer, "/" + versionId, HttpMethod.Get, null, http.RequestAborted);
        });
        group.MapPut("/{versionId}/metadata", async (string versionId, ProductVaultMetadataChange change, HttpContext http, CustomerOwnerClient owners, ProductEvidenceClient evidence) =>
        {
            Id(versionId); NoQuery(http);
            var (tenant, session, customer) = await Scope(http, owners);
            return await evidence.VaultAsync(tenant, session, customer, "/" + versionId + "/metadata", HttpMethod.Put, change, http.RequestAborted);
        });
        group.MapPost("/{documentId}/replacement", async (string documentId, ProductVaultReplacement change, HttpContext http, CustomerOwnerClient owners, TenantAccessClient access, ProductEvidenceClient evidence) =>
        {
            Id(documentId); Id(change.ExpectedVersionId); NoQuery(http);
            if (!TenantAccessClient.Identifier(change.OperationId, 80) || string.IsNullOrWhiteSpace(change.OriginalFileName) || change.OriginalFileName.Length > 255 || change.OriginalFileName.Any(c => char.IsControl(c) || c is '/' or '\\') || change.ContentBase64.Length > 1_398_104)
                throw new TenantBoundaryException("VAULT_REPLACEMENT_INVALID", 400);
            var (tenant, session, customer) = await Scope(http, owners);
            var allowed = await access.EvaluateCustomerAsync(http, "evidence.document.upload", customer);
            if (allowed.Outcome != "Allow" || allowed.AccessBasis != "Self") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var previous = await evidence.VaultAsync(tenant, session, customer, "/" + change.ExpectedVersionId, HttpMethod.Get, null, http.RequestAborted);
            if (!previous.TryGetProperty("documentId", out var id) || id.GetString() != documentId) throw new TenantBoundaryException("VAULT_NOT_FOUND", 404);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(change.ContentBase64); } catch (FormatException) { throw new TenantBoundaryException("VAULT_REPLACEMENT_INVALID", 400); }
            if (bytes.Length is < 1 or > 1_048_576) throw new TenantBoundaryException("EVIDENCE_CONTENT_SIZE_INVALID", 400);
            var stagingId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + ":" + customer + ":" + change.OperationId)))[..32];
            var staged = await evidence.StageAsync(stagingId, change.ContentBase64, change.ContentType, http.RequestAborted);
            var result = await evidence.VaultAsync(tenant, session, customer, "/" + documentId + "/replacement", HttpMethod.Post,
                new { change.OperationId, change.ExpectedVersionId, change.OriginalFileName, change.ContentType, ContentReference = staged.Reference, ContentSha256 = staged.Sha256 }, http.RequestAborted);
            if (!result.TryGetProperty("documentId", out var returnedDocument) || returnedDocument.GetString() != documentId ||
                !result.TryGetProperty("documentVersionId", out var returnedVersion) || returnedVersion.GetString() != "document-version-" + change.OperationId)
                throw new TenantBoundaryException("VAULT_OWNER_RESPONSE_INVALID", 503);
            return result;
        });
    }
    private static async Task<(string Tenant, string Session, string Customer)> Scope(HttpContext http, CustomerOwnerClient owners)
    {
        var session = await owners.SessionAsync(http);
        var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted) ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
        return (session.TenantId, session.AuthenticationContextId, customer.CustomerId);
    }
    private static void Id(string value) { if (!TenantAccessClient.Identifier(value, 160)) throw new TenantBoundaryException("VAULT_ID_INVALID", 400); }
    private static void NoQuery(HttpContext http) { if (http.Request.Query.Count != 0) throw new TenantBoundaryException("VAULT_QUERY_INVALID", 400); }
}

public sealed partial class ProductEvidenceClient
{
    public async Task<JsonElement> VaultAsync(string tenant, string session, string customer, string suffix, HttpMethod method, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, "local/v1/customers/" + Uri.EscapeDataString(customer) + "/document-vault" + suffix));
        request.Headers.Add("X-Monergy-Tenant", tenant); request.Headers.Add("X-Monergy-Session", session);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "VAULT_OWNER_UNAVAILABLE";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (error.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String && TenantAccessClient.Identifier(value.GetString(), 80)) code = value.GetString()!;
            }
            catch (JsonException) { }
            throw new TenantBoundaryException(code, (int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 422 ? (int)response.StatusCode : 503);
        }
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); }
        catch (JsonException) { throw new TenantBoundaryException("VAULT_OWNER_RESPONSE_INVALID", 503); }
    }
}
