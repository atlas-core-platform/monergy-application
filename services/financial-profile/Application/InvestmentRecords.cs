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
    Task<ReviewedInvestmentRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct);
    Task<InvestmentRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
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
        // Exact immutable source revision is a reference. Purchase and current values are explicitly reviewed separately.
        var normalized = input with { Investment = input.Investment with { Name = input.Investment.Name.Trim(), Institution = input.Investment.Institution.Trim(), InvestmentType = input.Investment.InvestmentType.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "investment-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedInvestmentRecord(id, input.OperationId, customer, normalized.Investment, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<InvestmentRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedInvestmentRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(id, 80)) throw new TenantBoundaryException("INVESTMENT_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("INVESTMENT_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
