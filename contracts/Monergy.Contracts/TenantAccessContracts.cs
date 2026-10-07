using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monergy.Contracts;

// AR-001 tenant-aware contracts. Legacy customer-scoped D13/D14 envelopes are
// retained; they cannot be substituted for this validated tenant/session path.
public sealed record TenantMembershipState(string TenantId, string ActorId, bool Active, long SubjectVersion, long PolicyVersion);
public sealed record TenantSessionLookup(string TenantId, string AuthenticationContextId);
public sealed record TenantSessionContext(string TenantId, string ActorId, string AuthenticationContextId,
    long SubjectVersion, DateTimeOffset ExpiresAt);
public sealed record TenantSessionEstablishment(string TenantId);
public sealed record AccessManagementEvent(string ContractId, Guid EventId, string EventName, string EventVersion,
    DateTimeOffset OccurredAt, string CorrelationId, string Producer, string TenantId, string AggregateId,
    string InitiatorActorId, long PolicyVersion, long? SubjectVersion, string Operation, string? SubjectActorId);
public sealed record AccessManagementReceipt(string Destination, string TenantId, Guid EventId, string ContractId,
    string EventVersion, string EventHash, string EvidenceReference, string Disposition);

public sealed class TenantAccessException(string code, int statusCode = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class TenantAccessProtocol
{
    public const string Version = "1.0.0";
    public const string Producer = "Access Management Service";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
    };

    public static void Identifier(string value, int maximum = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new TenantAccessException("INVALID_IDENTIFIER", 400);
    }

    public static void Validate(AccessManagementEvent message, string trustedTenant, string destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        Identifier(message.TenantId, 64);
        Identifier(message.AggregateId, 200);
        Identifier(message.InitiatorActorId);
        Identifier(message.CorrelationId, 256);
        Identifier(message.Operation, 100);
        if (message.SubjectActorId is not null) Identifier(message.SubjectActorId);
        var route = (message.ContractId, message.EventName, destination) is
            ("CID-068", "AuthorizationPolicyChanged", "authorization") or
            ("CID-069", "TenantUserAccessChanged", "authorization" or "sessions") or
            ("CID-070", "AccessManagementAuditEvidence", "audit");
        if (!route || message.TenantId != trustedTenant || message.Producer != Producer || message.EventVersion != Version ||
            message.EventId == Guid.Empty || message.OccurredAt == default || message.PolicyVersion < 1 || message.SubjectVersion is < 1 ||
            (message.ContractId == "CID-068" && message.AggregateId != trustedTenant) ||
            (message.ContractId == "CID-069" && (message.SubjectVersion is null || message.SubjectActorId is null || message.AggregateId != message.SubjectActorId)))
            throw new TenantAccessException("INVALID_ACCESS_EVENT");
    }

    public static string Hash(AccessManagementEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string?[] fields = ["Monergy.AM.Event.v1", message.ContractId, message.EventId.ToString("D"), message.EventName,
            message.EventVersion, message.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), message.CorrelationId,
            message.Producer, message.TenantId, message.AggregateId, message.InitiatorActorId,
            message.PolicyVersion.ToString(CultureInfo.InvariantCulture), message.SubjectVersion?.ToString(CultureInfo.InvariantCulture),
            message.Operation, message.SubjectActorId];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = field is null ? [] : Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, field is null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
