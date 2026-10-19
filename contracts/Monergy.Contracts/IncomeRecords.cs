using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncomeRecordDraft(string Name, string IncomeSource, string IncomeType,
    decimal Amount, string Frequency, string FinancialYear, DateOnly PeriodStart, DateOnly PeriodEnd,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateIncomeRecord(string OperationId, IncomeRecordDraft Income);
public sealed record ReviewedIncomeRecord(string IncomeRecordId, string OperationId, string CustomerId,
    IncomeRecordDraft Income, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, string VerificationStatus, int Revision = 1);
public sealed record IncomeRecordList(IReadOnlyList<ReviewedIncomeRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseIncomeRecord(string OperationId, int ExpectedRevision, IncomeRecordDraft Income);
public sealed record IncomeRecordHistory(IReadOnlyList<ReviewedIncomeRecord> Items, bool HasMore, int? NextBeforeRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompareIncomePeriods(string EarlierIncomeRecordId, int EarlierRevision, string LaterIncomeRecordId, int LaterRevision);
public sealed record IncomePeriodComparison(ReviewedIncomeRecord Earlier, ReviewedIncomeRecord Later, int PeriodDays);
