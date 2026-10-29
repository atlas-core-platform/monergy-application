using System.Text.Json.Serialization;

namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BankAccountRecordDraft(
    string BankName,
    string AccountType,
    string AccountNumberLast4,
    string SourceFinancialFactId,
    int SourceRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateBankAccountRecord(string OperationId, BankAccountRecordDraft Account);

public sealed record ReviewedBankAccountRecord(
    string BankAccountRecordId,
    string OperationId,
    string CustomerId,
    BankAccountRecordDraft Account,
    decimal Balance,
    string Currency,
    DateOnly BalanceAsOfDate,
    string SourceProvenanceId,
    string DocumentVersionId,
    string EvidenceId,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    int Revision = 1);

public sealed record BankAccountRecordList(IReadOnlyList<ReviewedBankAccountRecord> Items, bool HasMore);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseBankAccountRecord(
    string OperationId,
    int ExpectedRevision,
    BankAccountRecordDraft Account);

public sealed record BankAccountRecordHistory(
    IReadOnlyList<ReviewedBankAccountRecord> Items,
    bool HasMore,
    int? NextBeforeRevision);
