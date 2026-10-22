using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryLiabilityRecords : ILiabilityRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReviewedLiabilityRecord Record)> records = [];
    private readonly Dictionary<(string Tenant, string Customer, string Id), List<ReviewedLiabilityRecord>> revisions = [];
    private readonly Dictionary<(string Tenant, string Customer, string Actor, string Operation), (string Fingerprint, ReviewedLiabilityRecord Record)> operations = [];
    public Task<ReviewedLiabilityRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedLiabilityRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.LiabilityRecordId);
            if (operations.ContainsKey((tenant, customer, actor, record.OperationId)))
                throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedLiabilityRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedLiabilityRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.LiabilityRecordId);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("LIABILITY_RECORD_NOT_FOUND", 404);
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.TryGetValue(operation, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.LiabilityRecordId != record.LiabilityRecordId)
                    throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            if (records.Any(x => x.Key.Tenant == tenant && x.Key.Customer == customer && x.Value.Record.RecordedBy == actor && x.Value.Record.OperationId == record.OperationId))
                throw new TenantBoundaryException("LIABILITY_OPERATION_CONFLICT", 409);
            var history = revisions.GetValueOrDefault(key);
            if ((history?.LastOrDefault() ?? original.Record).Revision != expectedRevision)
                throw new TenantBoundaryException("LIABILITY_REVISION_CONFLICT", 409);
            if (history is null) revisions.Add(key, history = []);
            history.Add(record); operations.Add(operation, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedLiabilityRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var value)) return Task.FromResult<ReviewedLiabilityRecord?>(null);
            var history = revisions.GetValueOrDefault(key);
            return Task.FromResult(revision is null ? history?.LastOrDefault() ?? value.Record :
                revision == 1 ? value.Record : history?.SingleOrDefault(x => x.Revision == revision));
        }
    }
    public Task<LiabilityRecordSnapshot> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            // Filter latest versions across the complete owned record set before applying the view bound.
            var items=records.Where(x=>x.Key.Tenant==tenant && x.Key.Customer==customer)
                .OrderByDescending(x=>x.Value.Record.RecordedAt).ThenBy(x=>x.Key.Id,StringComparer.Ordinal)
                .Select(x=>revisions.GetValueOrDefault(x.Key)?.LastOrDefault() ?? x.Value.Record)
                .Where(x=>x.Currency==currency && x.Liability.BalanceAsOfDate==asOfDate).Take(201).ToArray();
            return Task.FromResult(new LiabilityRecordSnapshot(asOfDate,currency,items.Take(200).ToArray(),items.Length>200));
        }
    }
    public Task<LiabilityRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("LIABILITY_RECORD_NOT_FOUND", 404);
            var items = (revisions.GetValueOrDefault(key) ?? []).Append(original.Record)
                .Where(x => beforeRevision is null || x.Revision < beforeRevision).OrderByDescending(x => x.Revision).Take(51).ToArray();
            return Task.FromResult(new LiabilityRecordHistory(items.Take(50).ToArray(), items.Length > 50, items.Length > 50 ? items[49].Revision : null));
        }
    }
    public Task<LiabilityRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var anchor = afterId is null ? null : records.GetValueOrDefault((tenant, customer, afterId)).Record;
            if (afterId is not null && anchor is null) throw new TenantBoundaryException("RECORD_PAGE_NOT_FOUND", 404);
            var items = records.Where(x => x.Key.Tenant == tenant && x.Key.Customer == customer &&
                (anchor is null || x.Value.Record.RecordedAt < anchor.RecordedAt || x.Value.Record.RecordedAt == anchor.RecordedAt && string.CompareOrdinal(x.Key.Id, afterId) > 0))
                .OrderByDescending(x => x.Value.Record.RecordedAt).ThenBy(x => x.Key.Id, StringComparer.Ordinal).Take(201)
                .Select(x => revisions.GetValueOrDefault(x.Key)?.LastOrDefault() ?? x.Value.Record).ToArray();
            return Task.FromResult(new LiabilityRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
