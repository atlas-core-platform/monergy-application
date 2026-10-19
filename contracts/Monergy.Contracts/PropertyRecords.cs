using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PropertyRecordDraft(string Name, string Location, string PropertyType,
    decimal PurchaseValue, decimal CurrentValue, DateOnly ValueAsOfDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePropertyRecord(string OperationId, PropertyRecordDraft Property);
public sealed record ReviewedPropertyRecord(string PropertyRecordId, string OperationId, string CustomerId,
    PropertyRecordDraft Property, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, int Revision = 1);
public sealed record PropertyRecordList(IReadOnlyList<ReviewedPropertyRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RevisePropertyRecord(string OperationId, int ExpectedRevision, PropertyRecordDraft Property);
public sealed record PropertyRecordHistory(IReadOnlyList<ReviewedPropertyRecord> Items, bool HasMore, int? NextBeforeRevision);
