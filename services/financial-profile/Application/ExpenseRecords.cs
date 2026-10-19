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
    Task<ReviewedExpenseRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct);
    Task<ExpenseRecordList> ListAsync(string tenant, string customer, CancellationToken ct);
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
    public async Task<ReviewedExpenseRecord> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(id, 80)) throw new TenantBoundaryException("EXPENSE_RECORD_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("EXPENSE_RECORD_NOT_FOUND", 404);
    }
    private static bool Text(string? value, int maxLength=120) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);
    private static bool Money(decimal value) => value is >= 0 and <= 999999999999.99m && decimal.Round(value, 2) == value;
    private static bool Currency(string value) => value.Length == 3 && value.All(char.IsAsciiLetterUpper);
}
