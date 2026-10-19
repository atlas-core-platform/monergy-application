using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryIncomeRecords : IIncomeRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReviewedIncomeRecord Record)> records = [];
    public Task<ReviewedIncomeRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedIncomeRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.IncomeRecordId);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("INCOME_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedIncomeRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(records.TryGetValue((tenant, customer, id), out var value) ? value.Record : null);
    }
    public Task<IncomeRecordList> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var items = records.Where(x => x.Key.Tenant == tenant && x.Key.Customer == customer).Select(x => x.Value.Record)
                .OrderByDescending(x => x.RecordedAt).ThenBy(x => x.IncomeRecordId, StringComparer.Ordinal).Take(201).ToArray();
            return Task.FromResult(new IncomeRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
