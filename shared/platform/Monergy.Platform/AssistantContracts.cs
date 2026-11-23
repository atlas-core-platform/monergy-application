using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Monergy.Platform;

// Platform contracts are deliberately separate from tenant audit and tenant authorization.
public sealed record PlatformPrincipalContext(string PrincipalId, string Realm, long CredentialVersion,
    DateTimeOffset ExpiresAt, string AuthenticationContext, string Operation, string? ResourceId);
public sealed record PlatformPrincipalStatus(string PrincipalId, string Realm, long CredentialVersion, bool Active);
public sealed record PlatformContextValidation(string AuthenticationContext, string Operation, string? ResourceId);
public sealed record PlatformPrincipalValidation(string PrincipalId);
public sealed record PlatformReviewDecision(string DecisionId, bool Allowed, string? PrincipalId, string Realm, long CredentialVersion,
    string Capability, string Operation, string? ResourceId, long PolicyRevision, long GrantRevision,
    DateTimeOffset EvaluatedAt, DateTimeOffset ExpiresAt, string ReasonCode, string AuditState);
public sealed record PlatformAuditEnvelope(string SchemaVersion, string EventId, string OperationId,
    string Realm, string Producer, string ActorKind, string ActorId, string Capability, string? DecisionId,
    long PolicyRevision, long GrantRevision, long? RecordVersion, string Action, string Outcome,
    string ReasonCode, string? ResourceId, string? SourceAudience, DateTimeOffset OccurredAt);
public sealed record PlatformAuditReceipt(string EventId, string ReceiptId, DateTimeOffset RecordedAt, string State);

public static class AssistantContract
{
    public const string Realm = "MONERGY_PLATFORM";
    public const string Capability = "platform.assistant.feedback.review";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    public static bool TokenMatches(string? expected, string supplied) =>
        expected is { Length: >= 32 and <= 512 } && supplied.Length is >= 32 and <= 512 &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));

    public static void RequireWorkload(HttpContext http, string header, string? expected)
    {
        if (http.Request.Headers[header].Count != 1 || !TokenMatches(expected, http.Request.Headers[header].ToString()) ||
            http.Request.Headers.ContainsKey("Cookie") || http.Request.Headers.ContainsKey("Authorization") ||
            http.Request.Headers.ContainsKey("X-Monergy-Tenant") || http.Request.Headers.ContainsKey("X-Monergy-Session"))
            throw new TenantBoundaryException("WORKLOAD_AUTHORIZATION_REQUIRED", 403);
    }

    public static async Task<T> ReadAsync<T>(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.Query.Count != 0 || request.ContentType is null ||
            !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || request.ContentLength > limit)
            throw new TenantBoundaryException("ASSISTANT_REQUEST_INVALID", 400);
        using var buffer = new MemoryStream();
        var bytes = new byte[Math.Min(limit + 1, 4096)];
        while (true)
        {
            var read = await request.Body.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > limit) throw new TenantBoundaryException("ASSISTANT_REQUEST_TOO_LARGE", 413);
            buffer.Write(bytes, 0, read);
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            RequireUniqueFields(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TenantBoundaryException("ASSISTANT_REQUEST_INVALID", 400); }
    }

    private static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new JsonException();
                RequireUniqueFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueFields(item);
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool OpaqueId(string value) => Guid.TryParseExact(value, "D", out _) || Guid.TryParseExact(value, "N", out _);
    public static bool Code(string value, int max = 128) => value.Length is > 0 && value.Length <= max &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static void ValidateAudit(PlatformAuditEnvelope item, string producer)
    {
        string[] actions = producer == "am-platform"
            ? ["ReviewerGrant", "ReviewerRevoke", "ReviewerAuthorization", "ReviewerAuditRecovery"]
            : ["FeedbackRead", "FeedbackClassify", "FeedbackAssign", "FeedbackDuplicate", "FeedbackStatus", "FeedbackPrivacyDelete"];
        string[] reasons = ["GrantApplied", "RevokeApplied", "AlreadyGranted", "AlreadyRevoked", "EntitlementMissing",
            "EntitlementRevoked", "Authorized", "IdentityUnavailable", "IdentityInvalid", "ContextExpired",
            "PolicyRevisionConflict", "ReviewApplied", "ReleaseAuthorized", "PrivacyDeletion", "RetentionExpiry",
            "REVIEW_ASSIGNMENT", "REVIEW_REMOVAL", "SECURITY_RESPONSE", "DeliveryRetry", "DeliveryDiscovery", "AuditRecovery"];
        if (item.SchemaVersion != "1.0.0" || item.Realm != Realm || item.Producer != producer ||
            !OpaqueId(item.EventId) || !OpaqueId(item.OperationId) || item.Capability != Capability ||
            item.ActorKind is not ("PlatformPrincipal" or "OperatorWorkload") || !Code(item.ActorId) ||
            item.DecisionId is not null && !OpaqueId(item.DecisionId) || item.PolicyRevision < 0 || item.GrantRevision < 0 ||
            item.RecordVersion < 0 || !actions.Contains(item.Action, StringComparer.Ordinal) ||
            item.Outcome is not ("Allow" or "Deny" or "Committed" or "ReleasePrepared") ||
            !reasons.Contains(item.ReasonCode, StringComparer.Ordinal) || item.ResourceId is not null && !OpaqueId(item.ResourceId) ||
            producer == "ai-feedback" && item.ActorKind != "PlatformPrincipal" ||
            (item.Action is "ReviewerGrant" or "ReviewerRevoke") && (item.ActorKind != "OperatorWorkload" || item.ActorId != "local-reviewer-operator") ||
            item.SourceAudience is not (null or "product" or "platform") || item.OccurredAt.Offset != TimeSpan.Zero ||
            item.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new TenantBoundaryException("PLATFORM_AUDIT_CONTRACT_INVALID", 400);
    }
}
