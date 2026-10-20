using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface ITaxRecordRepository
{
    Task<ReviewedTaxRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedTaxRecord record, CancellationToken ct);
    Task<ReviewedTaxRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<TaxRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<ReviewedTaxRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedTaxRecord record, CancellationToken ct);
    Task<TaxRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class TaxRecordsApplication(IFinancialProfileRepository financial,
    ITaxRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateTaxRecord? input, DateTimeOffset now)
    {
        if (input?.Tax is not { } tax || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(tax.Name) || !Text(tax.Jurisdiction) ||
            !Text(tax.TaxType) || !Money(tax.Amount) || !Text(tax.TaxYearLabel,30) ||
            tax.AmountKind is not ("PAID" or "DUE" or "WITHHELD") ||
            tax.PeriodStart.Year < 1900 || tax.PeriodEnd < tax.PeriodStart ||
            tax.PeriodEnd > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(tax.SourceFinancialFactId, 160) || tax.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedTaxRecord> CreateAsync(string tenant, string customer, string actor,
        CreateTaxRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Tax.SourceFinancialFactId, input.Tax.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.TaxRecord)
            throw new TenantBoundaryException("TAX_RECORD_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("TAX_RECORD_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. The amount is explicitly reviewed as the total received for the stated period; no rate conversion is inferred.
        var normalized = input with { Tax = input.Tax with { Name = input.Tax.Name.Trim(), Jurisdiction = input.Tax.Jurisdiction.Trim(), TaxType = input.Tax.TaxType.Trim(), TaxYearLabel = input.Tax.TaxYearLabel.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "tax-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedTaxRecord(id, input.OperationId, customer, normalized.Tax, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow(),"CUSTOMER_REVIEWED");
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<TaxRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedTaxRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseTaxRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Tax), clock.GetUtcNow()))
            throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Tax.SourceFinancialFactId != input.Tax.SourceFinancialFactId || original.Tax.SourceRevision != input.Tax.SourceRevision)
            throw new TenantBoundaryException("TAX_RECORD_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Tax.SourceFinancialFactId, original.Tax.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.TaxRecord ||
            original.VerificationStatus != "CUSTOMER_REVIEWED" || source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("TAX_RECORD_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Tax = input.Tax with { Name = input.Tax.Name.Trim(), Jurisdiction = input.Tax.Jurisdiction.Trim(), TaxType = input.Tax.TaxType.Trim(), TaxYearLabel = input.Tax.TaxYearLabel.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Tax = normalized.Tax, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<TaxRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedTaxRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("TAX_RECORD_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("TAX_RECORD_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value, int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
