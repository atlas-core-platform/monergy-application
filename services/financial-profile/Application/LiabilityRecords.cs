using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface ILiabilityRecordRepository
{
    Task<ReviewedLiabilityRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedLiabilityRecord record, CancellationToken ct);
    Task<ReviewedLiabilityRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<LiabilityRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null);
    Task<LiabilityRecordSnapshot> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct);
    Task<ReviewedLiabilityRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedLiabilityRecord record, CancellationToken ct);
    Task<LiabilityRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class LiabilityRecordsApplication(IFinancialProfileRepository financial,
    ILiabilityRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateLiabilityRecord? input, DateTimeOffset now)
    {
        if (input?.Liability is not { } liability || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(liability.Name) || !Text(liability.Creditor) || !Money(liability.OutstandingAmount) ||
            liability.BalanceAsOfDate.Year < 1900 ||
            liability.BalanceAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(liability.SourceFinancialFactId, 160) || liability.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedLiabilityRecord> CreateAsync(string tenant, string customer, string actor,
        CreateLiabilityRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Liability.SourceFinancialFactId, input.Liability.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Loan)
            throw new TenantBoundaryException("LIABILITY_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("LIABILITY_SOURCE_UNAVAILABLE", 503);
        // The exact reviewed loan/debt source is a reference. The liability balance and date are entered explicitly, never inferred or copied from that source figure.
        var normalized = input with { Liability = input.Liability with { Name = input.Liability.Name.Trim(), Creditor = input.Liability.Creditor.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "liability-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedLiabilityRecord(id, input.OperationId, customer, normalized.Liability, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow(),"CUSTOMER_REVIEWED");
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<LiabilityRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));
    public async Task<ReviewedLiabilityRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseLiabilityRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Liability), clock.GetUtcNow()))
            throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Liability.SourceFinancialFactId != input.Liability.SourceFinancialFactId || original.Liability.SourceRevision != input.Liability.SourceRevision)
            throw new TenantBoundaryException("LIABILITY_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Liability.SourceFinancialFactId, original.Liability.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Loan ||
            original.VerificationStatus != "CUSTOMER_REVIEWED" || source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("LIABILITY_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Liability = input.Liability with { Name = input.Liability.Name.Trim(), Creditor = input.Liability.Creditor.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Liability = normalized.Liability, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<LiabilityRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedLiabilityRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("LIABILITY_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("LIABILITY_RECORD_NOT_FOUND", 404);
    }
    public Task<LiabilityRecordSnapshot> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct)
    {
        if (asOfDate.Year < 1900 || asOfDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(14)) || currency is null || !Currency(currency))
            throw new TenantBoundaryException("LIABILITY_SNAPSHOT_INVALID",400);
        return repository.SnapshotAsync(tenant,customer,asOfDate,currency,ct);
    }
    private static bool Text(string? value, int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
