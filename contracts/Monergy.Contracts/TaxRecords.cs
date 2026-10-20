using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TaxRecordDraft(string Name, string Jurisdiction, string TaxType,
    decimal Amount, string AmountKind, string TaxYearLabel, DateOnly PeriodStart, DateOnly PeriodEnd,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTaxRecord(string OperationId, TaxRecordDraft Tax);
public sealed record ReviewedTaxRecord(string TaxRecordId, string OperationId, string CustomerId,
    TaxRecordDraft Tax, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, string VerificationStatus, int Revision = 1);
public sealed record TaxRecordList(IReadOnlyList<ReviewedTaxRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseTaxRecord(string OperationId, int ExpectedRevision, TaxRecordDraft Tax);
public sealed record TaxRecordHistory(IReadOnlyList<ReviewedTaxRecord> Items, bool HasMore, int? NextBeforeRevision);
