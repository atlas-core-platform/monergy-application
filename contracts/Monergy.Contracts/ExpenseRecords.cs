using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExpenseRecordDraft(string Name, string Category, decimal Amount, DateOnly PeriodStart, DateOnly PeriodEnd,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateExpenseRecord(string OperationId, ExpenseRecordDraft Expense);
public sealed record ReviewedExpenseRecord(string ExpenseRecordId, string OperationId, string CustomerId,
    ExpenseRecordDraft Expense, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, string VerificationStatus, int Revision = 1);
public sealed record ExpenseRecordList(IReadOnlyList<ReviewedExpenseRecord> Items, bool HasMore);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseExpenseRecord(string OperationId, int ExpectedRevision, ExpenseRecordDraft Expense);
public sealed record ExpenseRecordHistory(IReadOnlyList<ReviewedExpenseRecord> Items, bool HasMore, int? NextBeforeRevision);
