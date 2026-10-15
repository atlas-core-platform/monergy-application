using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monergy.AccessManagement;

public sealed record AccessEventEnvelope(string ContractId, Guid EventId, string EventName, string EventVersion,
    DateTimeOffset OccurredAt, string CorrelationId, string Producer, string TenantId, string AggregateId,
    string InitiatorActorId, long PolicyVersion, long? SubjectVersion, string Operation, string? SubjectActorId);
public sealed record AccessEventReceipt(string Destination, string TenantId, Guid EventId, string ContractId,
    string EventVersion, string EventHash, string EvidenceReference, string Disposition);
public sealed record DeliverySummary(int Delivered, int Retrying, int DeadLettered, int LeaseLost);
public sealed record DeliveryStatus(string Status, long Count);
public sealed record DeliveryRedrive(Guid OperationId, Guid EventId, string Destination, int ExpectedAttempts, string OperatorReference);

public interface IAccessEventTransport
{
    Task<AccessEventReceipt> PublishAsync(string destination, AccessEventEnvelope message, CancellationToken ct);
}

public sealed class EventDeliveryException(string code, bool permanent = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Permanent { get; } = permanent;
}

public static class AccessEventProtocol
{
    public const string Version = "1.0.0";
    public const string Producer = "Access Management Service";
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false
    };

    public static string Hash(AccessEventEnvelope message)
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

    public static string Contract(string eventName) => eventName switch
    {
        "AuthorizationPolicyChanged" => "CID-068",
        "TenantUserAccessChanged" => "CID-069",
        "AccessManagementAuditEvidence" => "CID-070",
        _ => throw new EventDeliveryException("UNSUPPORTED_EVENT", true)
    };

    public static bool RoutesTo(string eventName, string destination) => (eventName, destination) is
        ("AuthorizationPolicyChanged", "authorization") or
        ("TenantUserAccessChanged", "authorization" or "sessions") or
        ("AccessManagementAuditEvidence", "audit");

    public static void Validate(AccessEventEnvelope message, string trustedTenant, string destination)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            AccessInput.Identifier(message.TenantId, 64);
            AccessInput.Identifier(message.AggregateId, 200);
            AccessInput.Identifier(message.InitiatorActorId);
            AccessInput.Identifier(message.CorrelationId, 256);
            AccessInput.Identifier(message.Operation, 100);
            if (message.SubjectActorId is not null) AccessInput.Identifier(message.SubjectActorId);
        }
        catch (AccessAdministrationException) { throw new EventDeliveryException("INVALID_EVENT", true); }
        if (message.TenantId != trustedTenant || message.Producer != Producer || message.EventVersion != Version ||
            message.EventId == Guid.Empty || message.ContractId != Contract(message.EventName) ||
            !RoutesTo(message.EventName, destination) || message.OccurredAt == default ||
            message.PolicyVersion < 1 || message.SubjectVersion is < 1 ||
            (message.EventName == "AuthorizationPolicyChanged" && message.AggregateId != message.TenantId) ||
            (message.EventName == "TenantUserAccessChanged" &&
                (message.SubjectVersion is null || message.SubjectActorId is null || message.AggregateId != message.SubjectActorId)))
            throw new EventDeliveryException("INVALID_EVENT", true);
    }

    public static void ValidateReceipt(AccessEventReceipt? receipt, AccessEventEnvelope message, string destination)
    {
        if (receipt is null || receipt.Destination != destination || receipt.TenantId != message.TenantId ||
            receipt.EventId != message.EventId || receipt.ContractId != message.ContractId || receipt.EventVersion != Version ||
            receipt.EventHash != Hash(message) || receipt.Disposition is not ("Applied" or "Duplicate") ||
            string.IsNullOrWhiteSpace(receipt.EvidenceReference) || receipt.EvidenceReference.Length > 200 ||
            receipt.EvidenceReference.Any(char.IsControl))
            throw new EventDeliveryException("INVALID_RECEIPT", true);
    }
}
