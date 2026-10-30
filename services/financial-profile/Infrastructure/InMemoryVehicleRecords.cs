using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;

public sealed class InMemoryVehicleRecords : IVehicleRecordRepository
{
    private readonly object sync = new();
    private readonly Dictionary<(string Tenant, string Customer, string Id), (string Fingerprint, ReviewedVehicleRecord Record)> records = [];
    private readonly Dictionary<(string Tenant, string Customer, string Id), List<ReviewedVehicleRecord>> revisions = [];
    private readonly Dictionary<(string Tenant, string Customer, string Actor, string Operation), (string Fingerprint, ReviewedVehicleRecord Record)> operations = [];
    public Task<ReviewedVehicleRecord> CreateAsync(string tenant, string customer, string actor,
        string fingerprint, ReviewedVehicleRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.VehicleRecordId);
            if (operations.ContainsKey((tenant, customer, actor, record.OperationId)))
                throw new TenantBoundaryException("VEHICLE_OPERATION_CONFLICT", 409);
            if (records.TryGetValue(key, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.RecordedBy != actor)
                    throw new TenantBoundaryException("VEHICLE_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            records.Add(key, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedVehicleRecord> ReviseAsync(string tenant, string customer, string actor,
        string fingerprint, int expectedRevision, ReviewedVehicleRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, record.VehicleRecordId);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("VEHICLE_RECORD_NOT_FOUND", 404);
            var operation = (tenant, customer, actor, record.OperationId);
            if (operations.TryGetValue(operation, out var prior))
            {
                if (prior.Fingerprint != fingerprint || prior.Record.VehicleRecordId != record.VehicleRecordId)
                    throw new TenantBoundaryException("VEHICLE_OPERATION_CONFLICT", 409);
                return Task.FromResult(prior.Record);
            }
            if (records.Any(x => x.Key.Tenant == tenant && x.Key.Customer == customer && x.Value.Record.RecordedBy == actor && x.Value.Record.OperationId == record.OperationId))
                throw new TenantBoundaryException("VEHICLE_OPERATION_CONFLICT", 409);
            var history = revisions.GetValueOrDefault(key);
            if ((history?.LastOrDefault() ?? original.Record).Revision != expectedRevision)
                throw new TenantBoundaryException("VEHICLE_REVISION_CONFLICT", 409);
            if (history is null) revisions.Add(key, history = []);
            history.Add(record); operations.Add(operation, (fingerprint, record));
            return Task.FromResult(record);
        }
    }
    public Task<ReviewedVehicleRecord?> ReadAsync(string tenant, string customer, string id, CancellationToken ct, int? revision = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var value)) return Task.FromResult<ReviewedVehicleRecord?>(null);
            var history = revisions.GetValueOrDefault(key);
            return Task.FromResult(revision is null ? history?.LastOrDefault() ?? value.Record :
                revision == 1 ? value.Record : history?.SingleOrDefault(x => x.Revision == revision));
        }
    }
    public Task<VehicleRecordList> SnapshotAsync(string tenant, string customer, DateOnly asOfDate, string currency, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            // Filter latest versions across the complete owned record set before applying the view bound.
            var items=records.Where(x=>x.Key.Tenant==tenant && x.Key.Customer==customer)
                .OrderByDescending(x=>x.Value.Record.RecordedAt).ThenBy(x=>x.Key.Id,StringComparer.Ordinal)
                .Select(x=>revisions.GetValueOrDefault(x.Key)?.LastOrDefault() ?? x.Value.Record)
                .Where(x=>x.Currency==currency && x.Vehicle.ValueAsOfDate==asOfDate &&
                    (x.OwnershipStatus!="DISPOSED" || x.OwnershipAsOfDate>asOfDate)).Take(201).ToArray();
            return Task.FromResult(new VehicleRecordList(items.Take(200).ToArray(),items.Length>200));
        }
    }
    public Task<VehicleRecordHistory> HistoryAsync(string tenant, string customer, string id, int? beforeRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            var key = (tenant, customer, id);
            if (!records.TryGetValue(key, out var original)) throw new TenantBoundaryException("VEHICLE_RECORD_NOT_FOUND", 404);
            var items = (revisions.GetValueOrDefault(key) ?? []).Append(original.Record)
                .Where(x => beforeRevision is null || x.Revision < beforeRevision).OrderByDescending(x => x.Revision).Take(51).ToArray();
            return Task.FromResult(new VehicleRecordHistory(items.Take(50).ToArray(), items.Length > 50, items.Length > 50 ? items[49].Revision : null));
        }
    }
    public Task<VehicleRecordList> ListAsync(string tenant, string customer, CancellationToken ct, string? afterId = null)
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
            return Task.FromResult(new VehicleRecordList(items.Take(200).ToArray(), items.Length > 200));
        }
    }
}
