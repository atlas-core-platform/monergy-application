using Monergy.Contracts;
using Monergy.Services.Audit.Application;

namespace Monergy.Services.Audit.Infrastructure;

public sealed class AppendOnlyInMemoryAuditRepository : IAuditEvidenceRepository
{
    public string AdapterKind => "IN_MEMORY_REFERENCE";

    private readonly object sync = new();
    private readonly HashSet<string> inbox = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuditEvidenceRecord> records = new(StringComparer.Ordinal);

    public Task<(AuditEvidenceRecord Record, bool Created)> AppendAsync(
        AuditableEvent source,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!inbox.Add(source.EventId))
            {
                return Task.FromResult((records[source.EventId], false));
            }

            var record = new AuditEvidenceRecord(
                $"audit-{source.EventId}",
                source.ContractId,
                source.EventId,
                source.EventName,
                source.Producer,
                source.SubjectType,
                source.SubjectId,
                source.OccurredAt,
                source.CorrelationId,
                source.CausationId,
                recordedAt);
            records.Add(source.EventId, record);
            return Task.FromResult((record, true));
        }
    }

    public Task<IReadOnlyList<AuditEvidenceRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<AuditEvidenceRecord>>(
                records.Values.OrderBy(record => record.RecordedAt).ToArray());
        }
    }
}
