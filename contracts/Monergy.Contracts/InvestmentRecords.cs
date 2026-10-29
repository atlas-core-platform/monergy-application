using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvestmentRecordDraft(string Name, string Institution, string InvestmentType,
    decimal PurchaseValue, decimal CurrentValue, DateOnly ValueAsOfDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInvestmentRecord(string OperationId, InvestmentRecordDraft Investment);
public sealed record ReviewedInvestmentRecord(string InvestmentRecordId, string OperationId, string CustomerId,
    InvestmentRecordDraft Investment, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, int Revision = 1,
    string LifecycleStatus = "UNREVIEWED", DateOnly? LifecycleAsOfDate = null);
public sealed record InvestmentRecordList(IReadOnlyList<ReviewedInvestmentRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseInvestmentRecord(string OperationId, int ExpectedRevision, InvestmentRecordDraft Investment);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewInvestmentRecordStatus(string OperationId, int ExpectedRevision, string Status, DateOnly AsOfDate);
public sealed record InvestmentRecordHistory(IReadOnlyList<ReviewedInvestmentRecord> Items, bool HasMore, int? NextBeforeRevision);
