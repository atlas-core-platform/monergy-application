using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monergy.AccessManagement;

// Deliberately separate from CapabilityCatalog, Tenant/Resource grants and Default Access Packs.
public static class PlatformReviewerContract
{
    public const string Capability = "platform.assistant.feedback.review";
    public const string Realm = "MONERGY_PLATFORM";
    public const string Risk = "Administrative";
    public const int MaximumPendingAudit = 1000;
    public const int MaximumDeliveryAttempts = 8;
    public const int MaximumRecoveryCycles = 3;
    public const int RecoveryAuditReserve = 16;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };

    public static bool IsPlatformCapability(string? capability) =>
        capability?.StartsWith("platform.", StringComparison.Ordinal) == true;

    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    public static bool ValidBinding(string operation, string? resourceId) => operation == "list"
        ? resourceId is null
        : operation is "read" or "classify" or "assign" or "duplicate" or "status" or "resolution" or "privacy-delete" &&
            Guid.TryParseExact(resourceId, "D", out _);

    public static void Require(bool valid, string code, int status = 400)
    {
        if (!valid) throw new PlatformAuthorizationException(code, status);
    }
}

public sealed class PlatformAuthorizationException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record PlatformReviewEvaluationRequest(string AuthenticationContext, string Operation, string? ResourceId);
public sealed record VerifiedPlatformContext(string PrincipalId, string Realm, long CredentialVersion,
    DateTimeOffset ExpiresAt, string AuthenticationContext, string Operation, string? ResourceId);
public sealed record VerifiedPlatformPrincipal(string PrincipalId, string Realm, long CredentialVersion, bool Active);
public sealed record PlatformReviewDecision(string DecisionId, bool Allowed, string? PrincipalId, string Realm,
    long CredentialVersion, string Capability, string Operation, string? ResourceId, long PolicyRevision, long GrantRevision,
    DateTimeOffset EvaluatedAt, DateTimeOffset ExpiresAt, string ReasonCode, string AuditState);
public sealed record PlatformEntitlementCommand(string ContractVersion, string OperationId, string PrincipalId, string Capability, string Action,
    long ExpectedPolicyRevision, string ReasonCode);
public sealed record PlatformEntitlementResult(string OperationId, string EntitlementId, bool Granted,
    long PolicyRevision, long GrantRevision, string AuditState);
public sealed record PlatformEntitlementStatus(long PolicyRevision, int PendingAudit);
public sealed record PlatformAuditDeliveryStatus(string OperationId, string EventId, string State, int Attempts,
    string? ReceiptId, DateTimeOffset? RecordedAt);
public sealed record PlatformAuditPendingEvent(string EventId, string OperationId, string Action, string Outcome,
    string State, int Attempts, int RecoveryCount, DateTimeOffset OccurredAt);
public sealed record PlatformAuditPendingPage(PlatformAuditPendingEvent[] Items, string? NextCursor);
public sealed record PlatformAuditRecoveryRequest(string OperationId, int ExpectedRecoveryCount);
public sealed record PlatformAuditRecoveryResult(string OperationId, string EventId, bool Resumed, int RecoveryCount, string AuditState);
public sealed record PlatformAuditEnvelope(string SchemaVersion, string EventId, string OperationId, string Realm,
    string Producer, string ActorKind, string ActorId, string Capability, string? DecisionId,
    long PolicyRevision, long GrantRevision, long? RecordVersion, string Action, string Outcome,
    string ReasonCode, string? ResourceId, string? SourceAudience, DateTimeOffset OccurredAt);
public sealed record PlatformAuditReceipt(string EventId, string ReceiptId, DateTimeOffset RecordedAt, string State);
