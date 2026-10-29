using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IInvestmentRecordRepository
{
    Task<ReviewedInvestmentRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedInvestmentRecord record, CancellationToken ct);
    Task<ReviewedInvestmentRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<InvestmentRecordList> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct);
    Task<InvestmentRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null);
    Task<ReviewedInvestmentRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedInvestmentRecord record, CancellationToken ct);
    Task<InvestmentRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class InvestmentRecordsApplication(IFinancialProfileRepository financial,
    IInvestmentRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateInvestmentRecord? input, DateTimeOffset now)
    {
        if (input?.Investment is not { } investment || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(investment.Name) || !Text(investment.Institution) ||
            !Text(investment.InvestmentType) || !Money(investment.PurchaseValue) || !Money(investment.CurrentValue) ||
            investment.ValueAsOfDate.Year < 1900 || investment.ValueAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(investment.SourceFinancialFactId, 160) || investment.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedInvestmentRecord> CreateAsync(string tenant, string customer, string actor,
        CreateInvestmentRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Investment.SourceFinancialFactId, input.Investment.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Investment)
            throw new TenantBoundaryException("INVESTMENT_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("INVESTMENT_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. Values remain explicitly customer reviewed.
        var normalized = input with { Investment = input.Investment with { Name = input.Investment.Name.Trim(), Institution = input.Investment.Institution.Trim(), InvestmentType = input.Investment.InvestmentType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "investment-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedInvestmentRecord(id, input.OperationId, customer, normalized.Investment, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<InvestmentRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));
    public async Task<ReviewedInvestmentRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseInvestmentRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Investment), clock.GetUtcNow()))
            throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Investment.SourceFinancialFactId != input.Investment.SourceFinancialFactId || original.Investment.SourceRevision != input.Investment.SourceRevision)
            throw new TenantBoundaryException("INVESTMENT_SOURCE_IMMUTABLE", 400);
        var previous = await repository.ReadAsync(tenant, customer, id, ct, input.ExpectedRevision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INVESTMENT_REVISION_CONFLICT", 409);
        if (previous.LifecycleStatus == "CLOSED" && input.Investment.ValueAsOfDate > previous.LifecycleAsOfDate)
            throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        await EnsureOriginalSourceAsync(original, customer, ct).ConfigureAwait(false);
        var normalized = input with { Investment = input.Investment with { Name = input.Investment.Name.Trim(), Institution = input.Investment.Institution.Trim(), InvestmentType = input.Investment.InvestmentType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Investment = normalized.Investment, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1,
            LifecycleStatus = previous.LifecycleStatus, LifecycleAsOfDate = previous.LifecycleAsOfDate };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedInvestmentRecord> ReviewStatusAsync(string tenant, string customer, string actor, string id,
        ReviewInvestmentRecordStatus input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue || !Guid.TryParseExact(input.OperationId, "N", out _) ||
            input.OperationId.Any(char.IsAsciiLetterUpper) || input.Status is not ("ACTIVE" or "CLOSED") || input.AsOfDate.Year < 1900 ||
            input.AsOfDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(14)))
            throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        var previous = await repository.ReadAsync(tenant, customer, id, ct, input.ExpectedRevision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INVESTMENT_REVISION_CONFLICT", 409);
        if (input.AsOfDate < previous.Investment.ValueAsOfDate || input.AsOfDate < previous.LifecycleAsOfDate)
            throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        await EnsureOriginalSourceAsync(original, customer, ct).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options)));
        var record = previous with { OperationId = input.OperationId, RecordedBy = actor, RecordedAt = clock.GetUtcNow(),
            Revision = input.ExpectedRevision + 1, LifecycleStatus = input.Status, LifecycleAsOfDate = input.AsOfDate };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    private async Task EnsureOriginalSourceAsync(ReviewedInvestmentRecord original, string customer, CancellationToken ct)
    {
        var source = await financial.GetFactAsync(original.Investment.SourceFinancialFactId, original.Investment.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Investment ||
            source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("INVESTMENT_SOURCE_UNAVAILABLE", 503);
    }
    public async Task<InvestmentRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedInvestmentRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INVESTMENT_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
