using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;

namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryCreditCardRecords : ICreditCardRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id),
        (string Fingerprint, ReviewedCreditCardRecord Record)> records = [];
    private readonly Dictionary<(string Tenant, string Customer, string Id),
        List<ReviewedCreditCardRecord>> revisions = [];
    private readonly Dictionary<(string Tenant, string Customer, string Actor, string Operation),
        (string Fingerprint, ReviewedCreditCardRecord Record)> operations = [];

    public Task<ReviewedCreditCardRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedCreditCardRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.ContainsKey(operation)) Conflict();
            var key = (tenant, customer, record.CreditCardRecordId);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor) Conflict();
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }

    public Task<ReviewedCreditCardRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedCreditCardRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.CreditCardRecordId);
            if (!records.TryGetValue(key, out var original)) NotFound();
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.TryGetValue(operation, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.CreditCardRecordId != record.CreditCardRecordId) Conflict();
                return Task.FromResult(prior.Record);
            }
            if (records.Any(item => item.Key.Tenant == tenant && item.Key.Customer == customer &&
                item.Value.Record.RecordedBy == actor && item.Value.Record.OperationId == record.OperationId)) Conflict();
            var history = revisions.GetValueOrDefault(key);
            if ((history?.LastOrDefault() ?? original.Record).Revision != expectedRevision)
                throw new TenantBoundaryException("CREDIT_CARD_REVISION_CONFLICT", 409);
            if (history is null) revisions.Add(key, history = []);
            history.Add(record); operations.Add(operation, (fingerprint, record));
            return Task.FromResult(record);
        }
    }

    public Task<ReviewedCreditCardRecord?> ReadAsync(string tenant, string customer, string id,
        CancellationToken ct, int? revision = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var value)) return Task.FromResult<ReviewedCreditCardRecord?>(null);
            var history = revisions.GetValueOrDefault(key);
            return Task.FromResult(revision is null ? history?.LastOrDefault() ?? value.Record :
                revision == 1 ? value.Record : history?.SingleOrDefault(item => item.Revision == revision));
        }
    }

    public Task<CreditCardRecordList> ListAsync(string tenant, string customer, CancellationToken ct,
        string? afterId = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var anchor = afterId is null ? null : records.GetValueOrDefault((tenant, customer, afterId)).Record;
            if (afterId is not null && anchor is null) throw new TenantBoundaryException("RECORD_PAGE_NOT_FOUND", 404);
            var items = records.Where(item => item.Key.Tenant == tenant && item.Key.Customer == customer &&
                (anchor is null || item.Value.Record.RecordedAt < anchor.RecordedAt ||
                item.Value.Record.RecordedAt == anchor.RecordedAt && string.CompareOrdinal(item.Key.Id, afterId) > 0))
                .OrderByDescending(item => item.Value.Record.RecordedAt).ThenBy(item => item.Key.Id, StringComparer.Ordinal)
                .Take(201).Select(item => revisions.GetValueOrDefault(item.Key)?.LastOrDefault() ?? item.Value.Record).ToArray();
            return Task.FromResult(new CreditCardRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }

    public Task<CreditCardRecordHistory> HistoryAsync(string tenant, string customer, string id,
        int? beforeRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var original)) NotFound();
            var items = (revisions.GetValueOrDefault(key) ?? []).Append(original.Record)
                .Where(item => beforeRevision is null || item.Revision < beforeRevision)
                .OrderByDescending(item => item.Revision).Take(51).ToArray();
            return Task.FromResult(new CreditCardRecordHistory(items.Take(50).ToArray(), items.Length > 50,
                items.Length > 50 ? items[49].Revision : null));
        }
    }

    private static void Conflict() => throw new TenantBoundaryException("CREDIT_CARD_OPERATION_CONFLICT", 409);
    private static void NotFound() => throw new TenantBoundaryException("CREDIT_CARD_RECORD_NOT_FOUND", 404);
}
