using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryGoalContributions : IGoalContributionRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReportedGoalContribution Record)> records = [];
    private readonly Dictionary<(string Tenant, string Customer, string Id), List<ReportedGoalContribution>> revisions = [];
    private readonly Dictionary<(string Tenant, string Customer, string Actor, string Operation), (string Fingerprint, ReportedGoalContribution Record)> operations = [];
    public Task<ReportedGoalContribution> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReportedGoalContribution record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.GoalContributionId);
            if (operations.ContainsKey((tenant, customer, actor, record.OperationId)))
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_OPERATION_CONFLICT", 409);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("GOAL_CONTRIBUTION_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReportedGoalContribution> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReportedGoalContribution record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.GoalContributionId);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("GOAL_CONTRIBUTION_NOT_FOUND", 404);
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.TryGetValue(operation, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.GoalContributionId != record.GoalContributionId)
                    throw new TenantBoundaryException("GOAL_CONTRIBUTION_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            if (records.Any(x => x.Key.Tenant == tenant && x.Key.Customer == customer && x.Value.Record.RecordedBy == actor && x.Value.Record.OperationId == record.OperationId))
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_OPERATION_CONFLICT", 409);
            var history = revisions.GetValueOrDefault(key);
            var current=history?.LastOrDefault() ?? original.Record;
            if (current.Revision != expectedRevision)
                throw new TenantBoundaryException("GOAL_CONTRIBUTION_REVISION_CONFLICT", 409);
            if(current.DeclarationStatus!="CUSTOMER_REPORTED") throw new TenantBoundaryException("GOAL_CONTRIBUTION_WITHDRAWN",409);
            if (history is null) revisions.Add(key, history = []);
            history.Add(record); operations.Add(operation, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReportedGoalContribution?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var value)) return Task.FromResult<ReportedGoalContribution?>(null);
            var history = revisions.GetValueOrDefault(key);
            return Task.FromResult(revision is null ? history?.LastOrDefault() ?? value.Record :
                revision == 1 ? value.Record : history?.SingleOrDefault(x => x.Revision == revision));
        }
    }
    public Task<GoalContributionHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("GOAL_CONTRIBUTION_NOT_FOUND", 404);
            var items = (revisions.GetValueOrDefault(key) ?? []).Append(original.Record)
                .Where(x => beforeRevision is null || x.Revision < beforeRevision).OrderByDescending(x => x.Revision).Take(51).ToArray();
            return Task.FromResult(new GoalContributionHistory(items.Take(50).ToArray(), items.Length > 50, items.Length > 50 ? items[49].Revision : null));
        }
    }
    public Task<GoalContributionList> ListAsync(string tenant, string customer,string financialGoalId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var items = records.Where(x => x.Key.Tenant == tenant && x.Key.Customer == customer && x.Value.Record.Contribution.FinancialGoalId==financialGoalId)
                .OrderByDescending(x => x.Value.Record.RecordedAt).ThenBy(x => x.Key.Id, StringComparer.Ordinal).Take(201)
                .Select(x => revisions.GetValueOrDefault(x.Key)?.LastOrDefault() ?? x.Value.Record).ToArray();
            return Task.FromResult(new GoalContributionList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
