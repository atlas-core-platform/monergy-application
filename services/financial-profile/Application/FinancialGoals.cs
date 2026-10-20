using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;

public interface IFinancialGoalRepository
{
    Task<CustomerFinancialGoal> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, CustomerFinancialGoal record, CancellationToken ct);
    Task<CustomerFinancialGoal?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null);
    Task<FinancialGoalList> ListAsync(string tenant, string customer, CancellationToken ct);
    Task<CustomerFinancialGoal> ReviseAsync(string tenant, string customer, string actor, string fingerprint,
        int expectedRevision, CustomerFinancialGoal record, CancellationToken ct);
    Task<FinancialGoalHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct);
}

public sealed class FinancialGoalsApplication(IFinancialGoalRepository repository, TimeProvider clock)
{
    public static bool Valid(CreateFinancialGoal? input) => input?.Goal is { } goal &&
        Guid.TryParseExact(input.OperationId,"N",out _) && !input.OperationId.Any(char.IsAsciiLetterUpper) &&
        !string.IsNullOrWhiteSpace(goal.Name) && goal.Name.Trim().Length <= 120 && !goal.Name.Any(char.IsControl) &&
        goal.TargetAmount is > 0 and <= 999999999999.99m && decimal.Round(goal.TargetAmount,2) == goal.TargetAmount &&
        goal.Currency is { Length:3 } && goal.Currency.All(char.IsAsciiLetterUpper) && goal.TargetDate.Year is >= 1900 and <= 2199;
    public async Task<CustomerFinancialGoal> CreateAsync(string tenant, string customer, string actor,
        CreateFinancialGoal input, CancellationToken ct)
    {
        if (!Valid(input)) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);
        var normalized = input with { Goal = input.Goal with { Name = input.Goal.Name.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var id = "goal-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var record = new CustomerFinancialGoal(id,input.OperationId,customer,normalized.Goal,actor,clock.GetUtcNow(),"CUSTOMER_DEFINED");
        return await repository.CreateAsync(tenant, customer, actor, fingerprint, record, ct).ConfigureAwait(false);
    }
    public Task<FinancialGoalList> ListAsync(string tenant, string customer, CancellationToken ct) => repository.ListAsync(tenant, customer, ct);
    public async Task<CustomerFinancialGoal> ReviseAsync(string tenant, string customer, string actor, string id,
        ReviseFinancialGoal input, CancellationToken ct)
    {
        if (input is null || input.ExpectedRevision is < 1 or int.MaxValue ||
            !Valid(new(input.OperationId, input.Goal)))
            throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);
        var original = await ReadAsync(tenant,customer,id,ct,1).ConfigureAwait(false);
        var normalized = input with { Goal = input.Goal with { Name = input.Goal.Name.Trim() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized, ContractJson.Options)));
        var record = original with { OperationId = input.OperationId, Goal = normalized.Goal, RecordedBy = actor,
            RecordedAt = clock.GetUtcNow(), Revision = input.ExpectedRevision + 1 };
        return await repository.ReviseAsync(tenant, customer, actor, fingerprint, input.ExpectedRevision, record, ct).ConfigureAwait(false);
    }
    public async Task<FinancialGoalHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        if (beforeRevision is < 1) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);
        await ReadAsync(tenant, customer, id, ct, 1).ConfigureAwait(false);
        return await repository.HistoryAsync(tenant, customer, id, beforeRevision, ct).ConfigureAwait(false);
    }
    public async Task<CustomerFinancialGoal> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        if (!TenantAccessClient.Identifier(id, 80) || revision is < 1) throw new TenantBoundaryException("FINANCIAL_GOAL_INVALID", 400);
        return await repository.ReadAsync(tenant, customer, id, ct, revision).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("FINANCIAL_GOAL_NOT_FOUND", 404);
    }
}
