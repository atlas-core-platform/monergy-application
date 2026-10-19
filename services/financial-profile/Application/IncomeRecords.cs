using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IIncomeRecordRepository
{
    Task<ReviewedIncomeRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedIncomeRecord record, CancellationToken ct);
    Task<ReviewedIncomeRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<IncomeRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<ReviewedIncomeRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedIncomeRecord record, CancellationToken ct);
    Task<IncomeRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class IncomeRecordsApplication(IFinancialProfileRepository financial,
    IIncomeRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateIncomeRecord? input, DateTimeOffset now)
    {
        if (input?.Income is not { } income || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(income.Name) || !Text(income.IncomeSource) ||
            !Text(income.IncomeType) || !Money(income.Amount) || !Text(income.FinancialYear,30) ||
            income.Frequency is not ("ONE_OFF" or "DAILY" or "WEEKLY" or "MONTHLY" or "QUARTERLY" or "ANNUAL" or "IRREGULAR") ||
            income.PeriodStart.Year < 1900 || income.PeriodEnd < income.PeriodStart ||
            income.PeriodEnd > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(income.SourceFinancialFactId, 160) || income.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedIncomeRecord> CreateAsync(string tenant, string customer, string actor,
        CreateIncomeRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("INCOME_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Income.SourceFinancialFactId, input.Income.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Income)
            throw new TenantBoundaryException("INCOME_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("INCOME_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. The amount is explicitly reviewed as the total received for the stated period; no rate conversion is inferred.
        var normalized = input with { Income = input.Income with { Name = input.Income.Name.Trim(), IncomeSource = input.Income.IncomeSource.Trim(), IncomeType = input.Income.IncomeType.Trim(), FinancialYear = input.Income.FinancialYear.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "income-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedIncomeRecord(id, input.OperationId, customer, normalized.Income, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow(),"CUSTOMER_REVIEWED");
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<IncomeRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedIncomeRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseIncomeRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Income), clock.GetUtcNow()))
            throw new TenantBoundaryException("INCOME_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Income.SourceFinancialFactId != input.Income.SourceFinancialFactId || original.Income.SourceRevision != input.Income.SourceRevision)
            throw new TenantBoundaryException("INCOME_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Income.SourceFinancialFactId, original.Income.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Income ||
            original.VerificationStatus != "CUSTOMER_REVIEWED" || source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("INCOME_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Income = input.Income with { Name = input.Income.Name.Trim(), IncomeSource = input.Income.IncomeSource.Trim(), IncomeType = input.Income.IncomeType.Trim(), FinancialYear = input.Income.FinancialYear.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Income = normalized.Income, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<IncomeRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("INCOME_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedIncomeRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("INCOME_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INCOME_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value, int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
