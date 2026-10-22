using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface ILoanRecordRepository
{
    Task<ReviewedLoanRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedLoanRecord record, CancellationToken ct);
    Task<ReviewedLoanRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<LoanRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null);
    Task<ReviewedLoanRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedLoanRecord record, CancellationToken ct);
    Task<LoanRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class LoanRecordsApplication(IFinancialProfileRepository financial,
    ILoanRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateLoanRecord? input, DateTimeOffset now)
    {
        if (input?.Loan is not { } loan || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(loan.Name) || !Text(loan.Lender) ||
            loan.LoanType is not ("HOME" or "PERSONAL" or "VEHICLE" or "EDUCATION" or "BUSINESS" or "OTHER") ||
            !Money(loan.SanctionAmount) || loan.SanctionAmount == 0 || !Money(loan.Outstanding) || !Money(loan.Emi) ||
            loan.InterestRatePercent is < 0 or > 100 || decimal.Round(loan.InterestRatePercent, 4) != loan.InterestRatePercent ||
            loan.TenureMonths is < 1 or > 1200 || loan.StartDate.Year < 1900 || loan.EndDate < loan.StartDate ||
            loan.BalanceAsOfDate < loan.StartDate || loan.BalanceAsOfDate > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(loan.SourceFinancialFactId, 160) || loan.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedLoanRecord> CreateAsync(string tenant, string customer, string actor,
        CreateLoanRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Loan.SourceFinancialFactId, input.Loan.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Loan)
            throw new TenantBoundaryException("LOAN_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("LOAN_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference, never a guessed outstanding/sanction/EMI mapping.
        var normalized = input with { Loan = input.Loan with { Name = input.Loan.Name.Trim(), Lender = input.Loan.Lender.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "loan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedLoanRecord(id, input.OperationId, customer, normalized.Loan, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow());
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<LoanRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null) => repository.ListAsync(tenant, customer, ct, ReviewedRecordPaging.Validate(afterId));
    public async Task<ReviewedLoanRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseLoanRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Loan), clock.GetUtcNow()))
            throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Loan.SourceFinancialFactId != input.Loan.SourceFinancialFactId || original.Loan.SourceRevision != input.Loan.SourceRevision)
            throw new TenantBoundaryException("LOAN_SOURCE_IMMUTABLE", 400);
        var previous = await repository.ReadAsync(tenant, customer, id, ct, input.ExpectedRevision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("LOAN_REVISION_CONFLICT", 409);
        if (previous.LifecycleAsOfDate is { } statusDate && (input.Loan.StartDate > statusDate || previous.LifecycleStatus == "CLOSED" && input.Loan.BalanceAsOfDate > statusDate))
            throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
        await EnsureOriginalSourceAsync(original, customer, ct).ConfigureAwait(false);
        var normalized = input with { Loan = input.Loan with { Name = input.Loan.Name.Trim(), Lender = input.Loan.Lender.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Loan = normalized.Loan, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1,
            LifecycleStatus = previous.LifecycleStatus, LifecycleAsOfDate = previous.LifecycleAsOfDate };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedLoanRecord> ReviewStatusAsync(string tenant, string customer, string actor, string id,
        ReviewLoanRecordStatus input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue || !Guid.TryParseExact(input.OperationId,"N",out _) ||
            input.OperationId.Any(char.IsAsciiLetterUpper) || input.Status is not ("ACTIVE" or "CLOSED") || input.AsOfDate.Year < 1900 ||
            input.AsOfDate > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(14)))
            throw new TenantBoundaryException("LOAN_RECORD_INVALID",400);
        var original = await ReadAsync(tenant,customer,id,ct,1).ConfigureAwait(false);
        var previous = await repository.ReadAsync(tenant,customer,id,ct,input.ExpectedRevision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("LOAN_REVISION_CONFLICT",409);
        if (input.AsOfDate < previous.Loan.StartDate || input.AsOfDate < previous.Loan.BalanceAsOfDate || input.AsOfDate < previous.LifecycleAsOfDate)
            throw new TenantBoundaryException("LOAN_RECORD_INVALID",400);
        await EnsureOriginalSourceAsync(original,customer,ct).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var record = previous with { OperationId = input.OperationId, RecordedBy = actor, RecordedAt = clock.GetUtcNow(),
            Revision = input.ExpectedRevision + 1, LifecycleStatus = input.Status, LifecycleAsOfDate = input.AsOfDate };
        return await repository.ReviseAsync(tenant,customer,actor,fingerprint,input.ExpectedRevision,record,ct).ConfigureAwait(false);
    }
    private async Task EnsureOriginalSourceAsync(ReviewedLoanRecord original, string customer, CancellationToken ct)
    {
        var source = await financial.GetFactAsync(original.Loan.SourceFinancialFactId, original.Loan.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Loan ||
            source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("LOAN_SOURCE_UNAVAILABLE", 503);
    }
    public async Task<LoanRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedLoanRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("LOAN_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("LOAN_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120 && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999999m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
