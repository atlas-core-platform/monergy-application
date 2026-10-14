using Monergy.Platform;
using Monergy.Services.Evidence.Infrastructure;
using System.Text;

namespace Monergy.Services.Evidence;

public sealed record ReferenceEvidenceContentRequest(string OperationId, string ContentBase64, string ContentType);

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
        TenantAccessIntegration.ValidateToken(token);
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
