using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryLoanRecords : ILoanRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReviewedLoanRecord Record)> records = [];
    public Task<ReviewedLoanRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedLoanRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.LoanRecordId);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("LOAN_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedLoanRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(records.TryGetValue((tenant, customer, id), out var value) ? value.Record : null);
    }
    public Task<LoanRecordList> ListAsync(string tenant, string customer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var items = records.Where(x => x.Key.Tenant == tenant && x.Key.Customer == customer).Select(x => x.Value.Record)
                .OrderByDescending(x => x.RecordedAt).ThenBy(x => x.LoanRecordId, StringComparer.Ordinal).Take(201).ToArray();
            return Task.FromResult(new LoanRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
