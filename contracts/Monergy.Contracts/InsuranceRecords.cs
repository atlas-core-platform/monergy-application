using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InsuranceRecordDraft(string Name, string Provider, string PolicyType,
    decimal SumAssured, decimal PremiumAmount, DateOnly PolicyAsOfDate, DateOnly? MaturityDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInsuranceRecord(string OperationId, InsuranceRecordDraft Insurance);
public sealed record ReviewedInsuranceRecord(string InsuranceRecordId, string OperationId, string CustomerId,
    InsuranceRecordDraft Insurance, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, int Revision = 1);
public sealed record InsuranceRecordList(IReadOnlyList<ReviewedInsuranceRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseInsuranceRecord(string OperationId, int ExpectedRevision, InsuranceRecordDraft Insurance);
public sealed record InsuranceRecordHistory(IReadOnlyList<ReviewedInsuranceRecord> Items, bool HasMore, int? NextBeforeRevision);
