using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoanRecordDraft(string Name, string Lender, string LoanType,
    decimal SanctionAmount, decimal Outstanding, decimal InterestRatePercent, decimal Emi,
    int TenureMonths, DateOnly StartDate, DateOnly EndDate, DateOnly BalanceAsOfDate,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLoanRecord(string OperationId, LoanRecordDraft Loan);
public sealed record ReviewedLoanRecord(string LoanRecordId, string OperationId, string CustomerId,
    LoanRecordDraft Loan, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt);
public sealed record LoanRecordList(IReadOnlyList<ReviewedLoanRecord> Items, bool HasMore);
