using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.FinancialProfile.Application;

public interface IBankAccountRecordRepository
{
    Task<ReviewedBankAccountRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedBankAccountRecord record, CancellationToken ct);
    Task<ReviewedBankAccountRecord?> ReadAsync(string tenant, string customer, string id,
        CancellationToken ct, int? revision = null);
    Task<BankAccountRecordList> ListAsync(string tenant, string customer, CancellationToken ct,
        string? afterId = null);
    Task<ReviewedBankAccountRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedBankAccountRecord record, CancellationToken ct);
    Task<BankAccountRecordHistory> HistoryAsync(string tenant, string customer, string id,
        int? beforeRevision, CancellationToken ct);
}

public sealed class BankAccountRecordsApplication(
    IFinancialProfileRepository financial,
    IBankAccountRecordRepository repository,
    TimeProvider clock)
{
    public static bool Valid(CreateBankAccountRecord? input)
    {
        var account = input?.Account;
        return account is not null &&
            Guid.TryParseExact(input!.OperationId, "N", out _) &&
            !input.OperationId.Any(char.IsAsciiLetterUpper) &&
            Text(account.BankName) && Text(account.AccountType) &&
            account.AccountNumberLast4.Length == 4 && account.AccountNumberLast4.All(char.IsAsciiDigit) &&
            TenantAccessClient.Identifier(account.SourceFinancialFactId, 160) &&
            account.SourceRevision > 0;
    }

    public async Task<ReviewedBankAccountRecord> CreateAsync(
        string tenant, string customer, string actor, CreateBankAccountRecord input, CancellationToken ct)
    {
        if (!Valid(input)) throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Account.SourceFinancialFactId,
            input.Account.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.BankAccount)
            throw new TenantBoundaryException("BANK_ACCOUNT_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (!SourceAvailable(source, provenance, customer))
            throw new TenantBoundaryException("BANK_ACCOUNT_SOURCE_UNAVAILABLE", 503);

        var normalized = input with { Account = Normalize(input.Account) };
        var fingerprint = Fingerprint(normalized);
        var id = "bank-account-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedBankAccountRecord(id, input.OperationId, customer, normalized.Account,
            source.Value, source.Currency, source.EffectiveDate, provenance!.FinancialProvenanceId,
            provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }

    public Task<BankAccountRecordList> ListAsync(string tenant, string customer, CancellationToken ct,
        string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));

    public async Task<ReviewedBankAccountRecord> ReviseAsync(string tenant, string customer, string actor,
        string id, ReviseBankAccountRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Account)))
            throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Account.SourceFinancialFactId != input.Account.SourceFinancialFactId ||
            original.Account.SourceRevision != input.Account.SourceRevision)
            throw new TenantBoundaryException("BANK_ACCOUNT_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Account.SourceFinancialFactId,
            original.Account.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (!SourceAvailable(source, provenance, customer) || source!.Value != original.Balance ||
            source.Currency != original.Currency || source.EffectiveDate != original.BalanceAsOfDate ||
            source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance!.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("BANK_ACCOUNT_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Account = Normalize(input.Account) };
        var record = original with { OperationId = input.OperationId, Account = normalized.Account,
            RecordedBy = actor, RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, Fingerprint(normalized),
            input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }

    public async Task<ReviewedBankAccountRecord> ReadAsync(string tenant, string customer, string id,
        CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 96) || revision is < 1)
            throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_NOT_FOUND", 404);
    }

    public async Task<BankAccountRecordHistory> HistoryAsync(string tenant, string customer, string id,
        int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("BANK_ACCOUNT_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }

    private static BankAccountRecordDraft Normalize(BankAccountRecordDraft account) => account with
    { BankName = account.BankName.Trim(), AccountType = account.AccountType.Trim() };
    private static string Fingerprint<T>(T input) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options)));
    private static bool SourceAvailable(FinancialFactRecord? source, FinancialProvenance? provenance,
        string customer) => source is not null && provenance is not null && source.CustomerId == customer &&
        source.FactType == FinancialObjectTypes.BankAccount && provenance.CustomerId == customer &&
        provenance.FinancialFactId == source.FinancialFactId && provenance.SourceFactId == source.SourceFactId &&
        source.Value is >= -999999999999999m and <= 999999999999999m &&
        decimal.Round(source.Value, 2) == source.Value && Currency(source.Currency) &&
        source.EffectiveDate.Year >= 1900;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
