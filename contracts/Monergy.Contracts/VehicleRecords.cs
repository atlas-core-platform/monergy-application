using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VehicleRecordDraft(string Name, string? RegistrationNumber, string VehicleType,
    decimal PurchaseValue, decimal CurrentValue, DateOnly ValueAsOfDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateVehicleRecord(string OperationId, VehicleRecordDraft Vehicle);
public sealed record ReviewedVehicleRecord(string VehicleRecordId, string OperationId, string CustomerId,
    VehicleRecordDraft Vehicle, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt);
public sealed record VehicleRecordList(IReadOnlyList<ReviewedVehicleRecord> Items, bool HasMore);
