using System.Net;

namespace Monergy.UatGateway;

// Translate only known, typed Evidence owner failures. Never expose raw
// upstream diagnostic payloads, provider URLs, or storage credentials.
internal static class ProductEvidenceDependencyFailure
{
    public static string StageCode(HttpStatusCode status, string? ownerCode) => status switch
    {
        HttpStatusCode.UnprocessableEntity => "EVIDENCE_SCAN_REJECTED",
        HttpStatusCode.ServiceUnavailable when ownerCode == "EVIDENCE_SCAN_UNAVAILABLE" =>
            "EVIDENCE_SCAN_UNAVAILABLE",
        HttpStatusCode.ServiceUnavailable when ownerCode == "EVIDENCE_STORAGE_UNAVAILABLE" =>
            "EVIDENCE_STORAGE_UNAVAILABLE",
        HttpStatusCode.ServiceUnavailable => "EVIDENCE_STAGING_UNAVAILABLE",
        HttpStatusCode.Conflict => "EVIDENCE_OPERATION_CONFLICT",
        _ => "EVIDENCE_STAGING_FAILED",
    };
}
