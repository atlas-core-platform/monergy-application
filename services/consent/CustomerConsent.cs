using Monergy.Platform;

namespace Monergy.Services.Consent;

public sealed record ConsentGrantWrite(string RequestId, long ExpectedVersion, string CustomerId, string ActorId,
    string Purpose, string[] CapabilityIds, DateTimeOffset ExpiresAt);
public sealed record ConsentRevocation(string RequestId, long ExpectedVersion);
public sealed record ConsentReceipt(string Id, string CustomerId, string ActorId, string Operation, long Version, string EvidenceReference);
public sealed record ConsentEvaluation(string TenantId, string ActorId, string CustomerId, string CapabilityId, string Purpose);
public sealed record ConsentDecision(string TenantId, string ActorId, string CustomerId, string CapabilityId, string Purpose,
    bool Allowed, string Reason, long Version, DateTimeOffset EvaluatedAt, DateTimeOffset? ExpiresAt);
public sealed record ConsentGrant(string Id, string CustomerId, string ActorId, string Purpose, string[] CapabilityIds,
    DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

public static class CustomerConsentPolicy
{
    public const string Purpose = "customer-advice";
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "financial-profile.profile.read", "financial-profile.profile.update", "consent.consent.read",
        "financial-rules.calculation.execute", "integration-gateway.connection.manage", "ai-intelligence.analysis.execute",
        "search.query.execute", "evidence.document.read", "evidence.document.upload", "reporting.report.read", "reporting.report.generate"
    };

    public static ConsentGrantWrite Validate(ConsentGrantWrite request, DateTimeOffset? now)
    {
        ValidateRevision(request.RequestId, request.ExpectedVersion);
        if (!TenantAccessClient.Identifier(request.CustomerId, 128) || !TenantAccessClient.Identifier(request.ActorId, 128) ||
            request.Purpose != Purpose || request.CapabilityIds is not { Length: > 0 and <= 32 } ||
            request.CapabilityIds.Any(value => !Capabilities.Contains(value)) ||
            request.CapabilityIds.Distinct(StringComparer.Ordinal).Count() != request.CapabilityIds.Length ||
            now is { } instant && (request.ExpiresAt <= instant || request.ExpiresAt > instant.AddDays(90)))
            throw new TenantBoundaryException("INVALID_CONSENT", 400);
        return request with { CapabilityIds = request.CapabilityIds.Order(StringComparer.Ordinal).ToArray(), ExpiresAt = request.ExpiresAt.ToUniversalTime() };
    }

    public static void ValidateRevision(string requestId, long expectedVersion)
    {
        if (!TenantAccessClient.Identifier(requestId, 128) || expectedVersion < 1)
            throw new TenantBoundaryException("INVALID_CONSENT_REVISION", 400);
    }
}

