using System.Text.Json.Serialization;

namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreditCardRecordDraft(
    string IssuerName,
    string CardType,
    string CardNumberLast4,
    string SourceFinancialFactId,
    int SourceRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCreditCardRecord(string OperationId, CreditCardRecordDraft Card);

public sealed record ReviewedCreditCardRecord(
    string CreditCardRecordId,
    string OperationId,
    string CustomerId,
    CreditCardRecordDraft Card,
    decimal Balance,
    string Currency,
    DateOnly BalanceAsOfDate,
    string SourceProvenanceId,
    string DocumentVersionId,
    string EvidenceId,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    int Revision = 1);

public sealed record CreditCardRecordList(IReadOnlyList<ReviewedCreditCardRecord> Items, bool HasMore);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviseCreditCardRecord(
    string OperationId,
    int ExpectedRevision,
    CreditCardRecordDraft Card);

public sealed record CreditCardRecordHistory(
    IReadOnlyList<ReviewedCreditCardRecord> Items,
    bool HasMore,
    int? NextBeforeRevision);
