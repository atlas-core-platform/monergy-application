using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IInsuranceRecordRepository
{
    Task<ReviewedInsuranceRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedInsuranceRecord record, CancellationToken ct);
    Task<ReviewedInsuranceRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<InsuranceRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<ReviewedInsuranceRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedInsuranceRecord record, CancellationToken ct);
    Task<InsuranceRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class InsuranceRecordsApplication(IFinancialProfileRepository financial,
    IInsuranceRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateInsuranceRecord? input, DateTimeOffset now)
    {
        if (input?.Insurance is not { } insurance || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(insurance.Name) || !Text(insurance.Provider) ||
            insurance.MaturityDate is { Year: < 1900 } || !Text(insurance.PolicyType) || !Money(insurance.SumAssured) || !Money(insurance.PremiumAmount) ||
            insurance.PolicyAsOfDate.Year < 1900 || insurance.PolicyAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(insurance.SourceFinancialFactId, 160) || insurance.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedInsuranceRecord> CreateAsync(string tenant, string customer, string actor,
        CreateInsuranceRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Insurance.SourceFinancialFactId, input.Insurance.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.InsurancePolicy)
            throw new TenantBoundaryException("INSURANCE_POLICY_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("INSURANCE_POLICY_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. Values remain explicitly customer reviewed.
        var normalized = input with { Insurance = input.Insurance with { Name = input.Insurance.Name.Trim(), Provider = input.Insurance.Provider.Trim(), PolicyType = input.Insurance.PolicyType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "insurance-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedInsuranceRecord(id, input.OperationId, customer, normalized.Insurance, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<InsuranceRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedInsuranceRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseInsuranceRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Insurance), clock.GetUtcNow()))
            throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Insurance.SourceFinancialFactId != input.Insurance.SourceFinancialFactId || original.Insurance.SourceRevision != input.Insurance.SourceRevision)
            throw new TenantBoundaryException("INSURANCE_POLICY_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Insurance.SourceFinancialFactId, original.Insurance.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.InsurancePolicy ||
            source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("INSURANCE_POLICY_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Insurance = input.Insurance with { Name = input.Insurance.Name.Trim(), Provider = input.Insurance.Provider.Trim(), PolicyType = input.Insurance.PolicyType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Insurance = normalized.Insurance, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<InsuranceRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedInsuranceRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INSURANCE_POLICY_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
