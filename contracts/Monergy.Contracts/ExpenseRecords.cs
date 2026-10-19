using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExpenseRecordDraft(string Name, string Category, decimal Amount, DateOnly PeriodStart, DateOnly PeriodEnd,
    string SourceFinancialFactId, int SourceRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateExpenseRecord(string OperationId, ExpenseRecordDraft Expense);
public sealed record ReviewedExpenseRecord(string ExpenseRecordId, string OperationId, string CustomerId,
    ExpenseRecordDraft Expense, string Currency, string SourceProvenanceId, string DocumentVersionId,
    string EvidenceId, string RecordedBy, DateTimeOffset RecordedAt, string VerificationStatus);
public sealed record ExpenseRecordList(IReadOnlyList<ReviewedExpenseRecord> Items, bool HasMore);
