using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryInvestmentRecords : IInvestmentRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReviewedInvestmentRecord Record)> records = [];
    private readonly Dictionary<(string Tenant, string Customer, string Id), List<ReviewedInvestmentRecord>> revisions = [];
    private readonly Dictionary<(string Tenant, string Customer, string Actor, string Operation), (string Fingerprint, ReviewedInvestmentRecord Record)> operations = [];
    public Task<ReviewedInvestmentRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedInvestmentRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.InvestmentRecordId);
            if (operations.ContainsKey((tenant, customer, actor, record.OperationId)))
                throw new TenantBoundaryException("INVESTMENT_OPERATION_CONFLICT", 409);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("INVESTMENT_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedInvestmentRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedInvestmentRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.InvestmentRecordId);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("INVESTMENT_RECORD_NOT_FOUND", 404);
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.TryGetValue(operation, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.InvestmentRecordId != record.InvestmentRecordId)
                    throw new TenantBoundaryException("INVESTMENT_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            if (records.Any(x => x.Key.Tenant == tenant && x.Key.Customer == customer && x.Value.Record.RecordedBy == actor && x.Value.Record.OperationId == record.OperationId))
                throw new TenantBoundaryException("INVESTMENT_OPERATION_CONFLICT", 409);
            var history = revisions.GetValueOrDefault(key);
            if ((history?.LastOrDefault() ?? original.Record).Revision != expectedRevision)
                throw new TenantBoundaryException("INVESTMENT_REVISION_CONFLICT", 409);
            if (history is null) revisions.Add(key, history = []);
            history.Add(record); operations.Add(operation, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedInvestmentRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var value)) return Task.FromResult<ReviewedInvestmentRecord?>(null);
            var history = revisions.GetValueOrDefault(key);
            return Task.FromResult(revision is null ? history?.LastOrDefault() ?? value.Record :
                revision == 1 ? value.Record : history?.SingleOrDefault(x => x.Revision == revision));
        }
    }
    public Task<InvestmentRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("INVESTMENT_RECORD_NOT_FOUND", 404);
            var items = (revisions.GetValueOrDefault(key) ?? []).Append(original.Record)
                .Where(x => beforeRevision is null || x.Revision < beforeRevision).OrderByDescending(x => x.Revision).Take(51).ToArray();
            return Task.FromResult(new InvestmentRecordHistory(items.Take(50).ToArray(), items.Length > 50, items.Length > 50 ? items[49].Revision : null));
        }
    }
    public Task<InvestmentRecordList> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var items = records.Where(x => x.Key.Tenant == tenant && x.Key.Customer == customer)
                .OrderByDescending(x => x.Value.Record.RecordedAt).ThenBy(x => x.Key.Id, StringComparer.Ordinal).Take(201)
                .Select(x => revisions.GetValueOrDefault(x.Key)?.LastOrDefault() ?? x.Value.Record).ToArray();
            return Task.FromResult(new InvestmentRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
