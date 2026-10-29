using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.FinancialProfile.Application;

public interface ICreditCardRecordRepository
{
    Task<ReviewedCreditCardRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedCreditCardRecord record, CancellationToken ct);
    Task<ReviewedCreditCardRecord?> ReadAsync(string tenant, string customer, string id,
        CancellationToken ct, int? revision = null);
    Task<CreditCardRecordList> ListAsync(string tenant, string customer, CancellationToken ct,
        string? afterId = null);
    Task<ReviewedCreditCardRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedCreditCardRecord record, CancellationToken ct);
    Task<CreditCardRecordHistory> HistoryAsync(string tenant, string customer, string id,
        int? beforeRevision, CancellationToken ct);
}

public sealed class CreditCardRecordsApplication(
    IFinancialProfileRepository financial,
    ICreditCardRecordRepository repository,
    TimeProvider clock)
{
    public static bool Valid(CreateCreditCardRecord? input)
    {
        var card = input?.Card;
        return card is not null &&
            Guid.TryParseExact(input!.OperationId, "N", out _) &&
            !input.OperationId.Any(char.IsAsciiLetterUpper) &&
            Text(card.IssuerName) && Text(card.CardType) &&
            card.CardNumberLast4.Length == 4 && card.CardNumberLast4.All(char.IsAsciiDigit) &&
            TenantAccessClient.Identifier(card.SourceFinancialFactId, 160) &&
            card.SourceRevision > 0;
    }

    public async Task<ReviewedCreditCardRecord> CreateAsync(
        string tenant, string customer, string actor, CreateCreditCardRecord input, CancellationToken ct)
    {
        if (!Valid(input)) throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Card.SourceFinancialFactId,
            input.Card.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Loan)
            throw new TenantBoundaryException("CREDIT_CARD_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (!SourceAvailable(source, provenance, customer))
            throw new TenantBoundaryException("CREDIT_CARD_SOURCE_UNAVAILABLE", 503);

        var normalized = input with { Card = Normalize(input.Card) };
        var fingerprint = Fingerprint(normalized);
        var id = "credit-card-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedCreditCardRecord(id, input.OperationId, customer, normalized.Card,
            source.Value, source.Currency, source.EffectiveDate, provenance!.FinancialProvenanceId,
            provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }

    public Task<CreditCardRecordList> ListAsync(string tenant, string customer, CancellationToken ct,
        string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));

    public async Task<ReviewedCreditCardRecord> ReviseAsync(string tenant, string customer, string actor,
        string id, ReviseCreditCardRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Card)))
            throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Card.SourceFinancialFactId != input.Card.SourceFinancialFactId ||
            original.Card.SourceRevision != input.Card.SourceRevision)
            throw new TenantBoundaryException("CREDIT_CARD_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Card.SourceFinancialFactId,
            original.Card.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (!SourceAvailable(source, provenance, customer) || source!.Value != original.Balance ||
            source.Currency != original.Currency || source.EffectiveDate != original.BalanceAsOfDate ||
            source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance!.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("CREDIT_CARD_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Card = Normalize(input.Card) };
        var record = original with { OperationId = input.OperationId, Card = normalized.Card,
            RecordedBy = actor, RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, Fingerprint(normalized),
            input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }

    public async Task<ReviewedCreditCardRecord> ReadAsync(string tenant, string customer, string id,
        CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 96) || revision is < 1)
            throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("CREDIT_CARD_RECORD_NOT_FOUND", 404);
    }

    public async Task<CreditCardRecordHistory> HistoryAsync(string tenant, string customer, string id,
        int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("CREDIT_CARD_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }

    private static CreditCardRecordDraft Normalize(CreditCardRecordDraft card) => card with
    { IssuerName = card.IssuerName.Trim(), CardType = card.CardType.Trim() };
    private static string Fingerprint<T>(T input) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options)));
    private static bool SourceAvailable(FinancialFactRecord? source, FinancialProvenance? provenance,
        string customer) => source is not null && provenance is not null && source.CustomerId == customer &&
        source.FactType == FinancialObjectTypes.Loan && provenance.CustomerId == customer &&
        provenance.FinancialFactId == source.FinancialFactId && provenance.SourceFactId == source.SourceFactId &&
        source.Value is >= 0 and <= 999999999999999m &&
        decimal.Round(source.Value, 2) == source.Value && Currency(source.Currency) &&
        source.EffectiveDate.Year >= 1900;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
