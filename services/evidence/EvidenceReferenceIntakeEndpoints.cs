using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using System.Text;

namespace Monergy.Services.Evidence;

public sealed record ReferenceEvidenceContentRequest(string OperationId, string ContentBase64, string ContentType, EvidenceSourceLocation? SourceLocation = null);
public sealed record ReferenceEvidenceReadRequest(string DocumentVersionId, string CustomerId);
public sealed record ReferenceEvidenceReadResult(
    string EvidenceId,
    string DocumentId,
    string DocumentVersionId,
    string CustomerId,
    string ContentReference,
    string ContentSha256,
    string ContentType,
    string Content,
    string? ContentBase64 = null);

public static class EvidenceReferenceIntakeEndpoints
{
    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "text/plain", "text/csv", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };

    public static IEndpointRouteBuilder MapEvidenceReferenceIntake(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        if (PhysicalPersistenceGuard.IsSelected(configuration)) PhysicalPersistenceGuard.EnsureAllowed(configuration);
        else ReferenceAdapterGuard.EnsureAllowed(configuration);
        var token = TenantAccessIntegration.Required(configuration, "Monergy:ProductIntake:EvidenceToken");
        var processingToken = TenantAccessIntegration.Required(configuration, "Monergy:ProductIntake:ProcessingToken");
        TenantAccessIntegration.ValidateToken(token);
        TenantAccessIntegration.ValidateToken(processingToken);
        if (string.Equals(token, processingToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence intake and processing require separate workload credentials.");
        var customerTenants = configuration.GetSection("Monergy:TenantBoundary:CustomerTenants").GetChildren()
            .ToDictionary(entry => entry.Key, entry => entry.Value ?? "", StringComparer.Ordinal);
        if (customerTenants.Count == 0 || customerTenants.Any(entry =>
                !TenantAccessClient.Identifier(entry.Key, 128) || !TenantAccessClient.Identifier(entry.Value, 64)))
            throw new InvalidOperationException("Evidence processing requires valid operator-owned customer/tenant bindings.");
        endpoints.MapPost("/internal/local/v1/evidence/content", async (
            HttpRequest request,
            ReferenceEvidenceContentRequest input,
            IEvidenceIntakeStore content, IEvidenceMalwareScanner scanner, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Product-Token"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!SupportedContentTypes.Contains(input.ContentType))
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_TYPE_UNSUPPORTED" });
            if (input.SourceLocation is { } location && (location.Provider is not ("google-drive" or "onedrive") ||
                new[] { location.FolderId, location.FileId, location.FileVersion }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 512 || x.Any(char.IsControl))))
                return Results.BadRequest(new { error = "EVIDENCE_SOURCE_LOCATION_INVALID" });
            if (input.OperationId is null || input.OperationId.Length != 32 || input.OperationId.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
                return Results.BadRequest(new { error = "EVIDENCE_OPERATION_INVALID" });
            byte[] value;
            try { value = Convert.FromBase64String(input.ContentBase64); }
            catch (Exception failure) when (failure is FormatException or ArgumentNullException) { return Results.BadRequest(new { error = "EVIDENCE_CONTENT_INVALID" }); }
            if (value.Length is < 1 or > 1_048_576)
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_SIZE_INVALID" });
            if (!ContentMatchesType(value, input.ContentType))
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_TYPE_MISMATCH" });
            try
            {
                var check = await scanner.CheckAsync(value, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Results.Ok(await content.StageScannedAsync(input.OperationId, value, input.ContentType, check, input.SourceLocation, ct));
            }
            catch (EvidenceScanRejectedException)
            { return Results.Json(new { error = "EVIDENCE_SCAN_REJECTED" }, statusCode: 422); }
            catch (EvidenceScanUnavailableException)
            { return Results.Json(new { error = "EVIDENCE_SCAN_UNAVAILABLE" }, statusCode: 503); }
            catch (EvidenceStorageUnavailableException)
            { return Results.Json(new { error = "EVIDENCE_STORAGE_UNAVAILABLE" }, statusCode: 503); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "EVIDENCE_OPERATION_CONFLICT" }); }
        });
        endpoints.MapPost("/internal/local/v1/evidence/processing-source", async (
            HttpRequest request,
            ReferenceEvidenceReadRequest input,
            IEvidenceRepository repository,
            IEvidenceIntakeStore content,
            CancellationToken cancellationToken) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Processing-Token"].ToString(), processingToken))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!TenantAccessClient.Identifier(input.DocumentVersionId, 160) ||
                !TenantAccessClient.Identifier(input.CustomerId, 128))
                return Results.BadRequest(new { error = "EVIDENCE_PROCESSING_SOURCE_INVALID" });
            var tenants = request.Headers["X-Monergy-Tenant"];
            if (tenants.Count != 1 || !TenantAccessClient.Identifier(tenants[0], 64) ||
                customerTenants.TryGetValue(input.CustomerId, out var owningTenant) &&
                !string.Equals(tenants[0], owningTenant, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var version = await repository.GetVersionAsync(input.DocumentVersionId, cancellationToken).ConfigureAwait(false);
            if (version is null || !string.Equals(version.CustomerId, input.CustomerId, StringComparison.Ordinal))
                return Results.NotFound(new { error = "EVIDENCE_PROCESSING_SOURCE_NOT_FOUND" });
            // Newly registered customers have no static fixture mapping. Their
            // immutable Evidence tenant, written behind the owner boundary, is
            // authoritative. Legacy unbound fixtures still require the mapping.
            if (version.TenantId is not null ? version.TenantId != tenants[0] : !customerTenants.ContainsKey(input.CustomerId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!string.Equals(version.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(version.ContentType, "text/csv", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(version.ContentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(version.ContentType, "image/png", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(version.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(version.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "EVIDENCE_PROCESSING_CONTENT_UNSUPPORTED" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            byte[]? value;
            try { value = await content.ReadRegisteredAsync(version, cancellationToken).ConfigureAwait(false); }
            catch (EvidenceStorageUnavailableException) { return Results.Json(new { error = "EVIDENCE_PROCESSING_CONTENT_UNAVAILABLE" }, statusCode: 503); }
            if (value is null || value.LongLength != version.ContentLength ||
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(value)) != version.ContentSha256)
                return Results.Json(new { error = "EVIDENCE_PROCESSING_CONTENT_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            var binary = version.ContentType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase) ||
                version.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                version.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
                version.ContentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new ReferenceEvidenceReadResult(
                version.EvidenceId,
                version.DocumentId,
                version.DocumentVersionId,
                version.CustomerId,
                version.ContentReference,
                version.ContentSha256,
                version.ContentType,
                binary ? "" : System.Text.Encoding.UTF8.GetString(value),
                binary ? Convert.ToBase64String(value) : null));
        });
        return endpoints;
    }

    private static bool ContentMatchesType(byte[] content, string contentType)
    {
        if (contentType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase))
            return XlsxEvidenceValidation.Valid(content);
        if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return content.AsSpan().StartsWith("%PDF-"u8);
        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            return content.AsSpan().StartsWith(signature);
        }
        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = [0xFF, 0xD8, 0xFF];
            return content.AsSpan().StartsWith(signature);
        }
        if (!(contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
              contentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase)) || content.Contains((byte)0))
            return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
