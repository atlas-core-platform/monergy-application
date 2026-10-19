using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiabilityRecordDraft(string Name, string Creditor, decimal OutstandingAmount, DateOnly BalanceAsOfDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLiabilityRecord(string OperationId, LiabilityRecordDraft Liability);
public sealed record ReviewedLiabilityRecord(string LiabilityRecordId, string OperationId, string CustomerId,
    LiabilityRecordDraft Liability, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, string VerificationStatus);
public sealed record LiabilityRecordList(IReadOnlyList<ReviewedLiabilityRecord> Items, bool HasMore);
