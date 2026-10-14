using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;
using System.Text;

namespace Monergy.Services.Evidence;

public sealed record ReferenceEvidenceContentRequest(string OperationId, string ContentBase64, string ContentType);
public sealed record ReferenceEvidenceReadRequest(string DocumentVersionId, string CustomerId);
public sealed record ReferenceEvidenceReadResult(
    string EvidenceId,
    string DocumentId,
    string DocumentVersionId,
    string CustomerId,
    string ContentReference,
    string ContentSha256,
    string ContentType,
    string Content);

public static class EvidenceReferenceIntakeEndpoints
{
    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "text/plain" };

    public static IEndpointRouteBuilder MapEvidenceReferenceIntake(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
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
        endpoints.MapPost("/internal/local/v1/evidence/content", (
            HttpRequest request,
            ReferenceEvidenceContentRequest input,
            ReferenceEvidenceContentStore content) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Product-Token"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!SupportedContentTypes.Contains(input.ContentType))
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_TYPE_UNSUPPORTED" });
            if (input.OperationId.Length != 32 || input.OperationId.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
                return Results.BadRequest(new { error = "EVIDENCE_OPERATION_INVALID" });
            byte[] value;
            try { value = Convert.FromBase64String(input.ContentBase64); }
            catch (FormatException) { return Results.BadRequest(new { error = "EVIDENCE_CONTENT_INVALID" }); }
            if (value.Length is < 1 or > 1_048_576)
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_SIZE_INVALID" });
            if (!ContentMatchesType(value, input.ContentType))
                return Results.BadRequest(new { error = "EVIDENCE_CONTENT_TYPE_MISMATCH" });
            try { return Results.Ok(content.Stage(input.OperationId, value, input.ContentType)); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "EVIDENCE_OPERATION_CONFLICT" }); }
        });
        endpoints.MapPost("/internal/local/v1/evidence/processing-source", async (
            HttpRequest request,
            ReferenceEvidenceReadRequest input,
            IEvidenceRepository repository,
            ReferenceEvidenceContentStore content,
            CancellationToken cancellationToken) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Processing-Token"].ToString(), processingToken))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!TenantAccessClient.Identifier(input.DocumentVersionId, 160) ||
                !TenantAccessClient.Identifier(input.CustomerId, 128))
                return Results.BadRequest(new { error = "EVIDENCE_PROCESSING_SOURCE_INVALID" });
            var tenants = request.Headers["X-Monergy-Tenant"];
            if (tenants.Count != 1 || !TenantAccessClient.Identifier(tenants[0], 64) ||
                !customerTenants.TryGetValue(input.CustomerId, out var owningTenant) ||
                !string.Equals(tenants[0], owningTenant, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var version = await repository.GetVersionAsync(input.DocumentVersionId, cancellationToken).ConfigureAwait(false);
            if (version is null || !string.Equals(version.CustomerId, input.CustomerId, StringComparison.Ordinal))
                return Results.NotFound(new { error = "EVIDENCE_PROCESSING_SOURCE_NOT_FOUND" });
            if (!string.Equals(version.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "EVIDENCE_PROCESSING_CONTENT_UNSUPPORTED" }, statusCode: StatusCodes.Status422UnprocessableEntity);
            var value = await content.ReadAsync(version.ContentReference, cancellationToken).ConfigureAwait(false);
            if (value is null)
                return Results.Json(new { error = "EVIDENCE_PROCESSING_CONTENT_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new ReferenceEvidenceReadResult(
                version.EvidenceId,
                version.DocumentId,
                version.DocumentVersionId,
                version.CustomerId,
                version.ContentReference,
                version.ContentSha256,
                version.ContentType,
                value));
        });
        return endpoints;
    }

    private static bool ContentMatchesType(byte[] content, string contentType)
    {
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
        if (!contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || content.Contains((byte)0))
            return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
