using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IExpenseRecordRepository
{
    Task<ReviewedExpenseRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedExpenseRecord record, CancellationToken ct);
    Task<ReviewedExpenseRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<ExpenseRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<ReviewedExpenseRecord> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, ReviewedExpenseRecord record, CancellationToken ct);
    Task<ExpenseRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class ExpenseRecordsApplication(IFinancialProfileRepository financial,
    IExpenseRecordRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateExpenseRecord? input, DateTimeOffset now)
    {
        if (input?.Expense is not { } expense || !Guid.TryParseExact(input.OperationId, "N", out _) || input.OperationId.Any(char.IsAsciiLetterUpper) ||
            !Text(expense.Name) || !Text(expense.Category) || !Money(expense.Amount) ||
            expense.PeriodStart.Year < 1900 || expense.PeriodEnd < expense.PeriodStart ||
            expense.PeriodEnd > DateOnly.FromDateTime(now.UtcDateTime.AddHours(14)) ||
            !TenantAccessClient.Identifier(expense.SourceFinancialFactId, 160) || expense.SourceRevision < 1)
            return false;
        return true;
    }
    public async Task<ReviewedExpenseRecord> CreateAsync(string tenant, string customer, string actor,
        CreateExpenseRecord input, CancellationToken ct)
    {
        if (!Valid(input, clock.GetUtcNow())) throw new TenantBoundaryException("EXPENSE_RECORD_INVALID", 400);
        var source = await financial.GetFactAsync(input.Expense.SourceFinancialFactId, input.Expense.SourceRevision, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Expense)
            throw new TenantBoundaryException("EXPENSE_SOURCE_NOT_FOUND", 404);
        var provenance = await financial.GetProvenanceAsync(source.FinancialProvenanceId, ct).ConfigureAwait(false);
        if (provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || !Currency(source.Currency))
            throw new TenantBoundaryException("EXPENSE_SOURCE_UNAVAILABLE", 503);
        // Exact immutable source revision is a reference. The amount is explicitly reviewed as the total spent for the stated period; no recurrence or annualisation is inferred.
        var normalized = input with { Expense = input.Expense with { Name = input.Expense.Name.Trim(), Category = input.Expense.Category.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "expense-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new ReviewedExpenseRecord(id, input.OperationId, customer, normalized.Expense, source.Currency,
            provenance.FinancialProvenanceId, provenance.DocumentVersionId, provenance.EvidenceId, actor, clock.GetUtcNow(),"CUSTOMER_REVIEWED");
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<ExpenseRecordList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<ReviewedExpenseRecord> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseExpenseRecord input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Expense), clock.GetUtcNow()))
            throw new TenantBoundaryException("EXPENSE_RECORD_INVALID", 400);
        var original = await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        if (original.Expense.SourceFinancialFactId != input.Expense.SourceFinancialFactId || original.Expense.SourceRevision != input.Expense.SourceRevision)
            throw new TenantBoundaryException("EXPENSE_SOURCE_IMMUTABLE", 400);
        var source = await financial.GetFactAsync(original.Expense.SourceFinancialFactId, original.Expense.SourceRevision, ct).ConfigureAwait(false);
        var provenance = await financial.GetProvenanceAsync(original.SourceProvenanceId, ct).ConfigureAwait(false);
        if (source is null || source.CustomerId != customer || source.FactType != FinancialObjectTypes.Expense ||
            original.VerificationStatus != "CUSTOMER_REVIEWED" || source.Currency != original.Currency || source.FinancialProvenanceId != original.SourceProvenanceId ||
            provenance is null || provenance.CustomerId != customer || provenance.FinancialFactId != source.FinancialFactId ||
            provenance.SourceFactId != source.SourceFactId || provenance.EvidenceId != original.EvidenceId || provenance.DocumentVersionId != original.DocumentVersionId)
            throw new TenantBoundaryException("EXPENSE_SOURCE_UNAVAILABLE", 503);
        var normalized = input with { Expense = input.Expense with { Name = input.Expense.Name.Trim(), Category = input.Expense.Category.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Expense = normalized.Expense, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<ExpenseRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("EXPENSE_RECORD_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<ReviewedExpenseRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("EXPENSE_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("EXPENSE_RECORD_NOT_FOUND", 404);
    }
    public async Task<ExpensePeriodComparison> CompareAsync(string tenant, string customer, CompareExpensePeriods input, CancellationToken ct)
    {
        if (input is null || !TenantAccessClient.Identifier(input.EarlierExpenseRecordId,80) || !TenantAccessClient.Identifier(input.LaterExpenseRecordId,80) || input.EarlierRevision < 1 || input.LaterRevision < 1)
            throw new TenantBoundaryException("EXPENSE_COMPARISON_INVALID",400);
        if (input.EarlierExpenseRecordId == input.LaterExpenseRecordId)
            throw new TenantBoundaryException("EXPENSE_COMPARISON_DISTINCT_RECORDS_REQUIRED",400);
        // Read the exact immutable versions the customer selected; later corrections do not change this view.
        var earlier = await ReadAsync(tenant,customer,input.EarlierExpenseRecordId,ct,input.EarlierRevision).ConfigureAwait(false);
        var later = await ReadAsync(tenant,customer,input.LaterExpenseRecordId,ct,input.LaterRevision).ConfigureAwait(false);
        if (earlier.VerificationStatus != "CUSTOMER_REVIEWED" || later.VerificationStatus != "CUSTOMER_REVIEWED")
            throw new TenantBoundaryException("EXPENSE_RECORD_UNAVAILABLE",503);
        if (earlier.Expense.SourceFinancialFactId == later.Expense.SourceFinancialFactId)
            throw new TenantBoundaryException("EXPENSE_COMPARISON_DISTINCT_SOURCES_REQUIRED",400);
        if (earlier.Currency != later.Currency) throw new TenantBoundaryException("EXPENSE_COMPARISON_CURRENCY_MISMATCH",400);
        if (!string.Equals(earlier.Expense.Category,later.Expense.Category,StringComparison.OrdinalIgnoreCase))
            throw new TenantBoundaryException("EXPENSE_COMPARISON_CATEGORY_MISMATCH",400);
        var days = earlier.Expense.PeriodEnd.DayNumber - earlier.Expense.PeriodStart.DayNumber + 1;
        if (days < 1 || days != later.Expense.PeriodEnd.DayNumber - later.Expense.PeriodStart.DayNumber + 1 || earlier.Expense.PeriodEnd >= later.Expense.PeriodStart)
            throw new TenantBoundaryException("EXPENSE_COMPARISON_PERIODS_NOT_COMPARABLE",400);
        // Date eligibility only. Financial arithmetic/aggregation remains owned by Financial Rules.
        return new(earlier,later,days);
    }
    private static bool Text(string? value, int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
