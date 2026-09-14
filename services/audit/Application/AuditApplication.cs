using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.Audit.Application;

public sealed record AuditEvidenceRecord(
    string AuditEvidenceId,
    string SourceContractId,
    string SourceEventId,
    string EventName,
    string Producer,
    string SubjectType,
    string SubjectId,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? CausationId,
    DateTimeOffset RecordedAt);

public interface IAuditEvidenceRepository
{
    Task<(AuditEvidenceRecord Record, bool Created)> AppendAsync(
        AuditableEvent source,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvidenceRecord>> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class AuditApplication(
    IAuditEvidenceRepository repository,
    ILifecycleTelemetry telemetry,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AcceptedContracts =
        new(StringComparer.Ordinal) { "CID-023", "CID-024", "CID-028", "CID-034", "CID-035" };

    public async Task<AuditEvidenceRecord> ConsumeAsync(
        AuditableEvent source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!AcceptedContracts.Contains(source.ContractId) ||
            string.IsNullOrWhiteSpace(source.EventId) ||
            string.IsNullOrWhiteSpace(source.CorrelationId) ||
            string.IsNullOrWhiteSpace(source.SubjectId))
        {
            throw new ArgumentException("The source event is not an auditable VS-02 fact.", nameof(source));
        }

        var appended = await repository.AppendAsync(source, timeProvider.GetUtcNow(), cancellationToken);
        telemetry.Record(new LifecycleSignal(
            "Audit Service",
            "ConsumeGovernedEvent",
            appended.Created ? "APPENDED" : "REPLAYED",
            source.EventId,
            source.CorrelationId,
            source.CausationId,
            source.SubjectType,
            source.SubjectId,
            "IN_MEMORY_REFERENCE"));
        return appended.Record;
    }

    public Task<IReadOnlyList<AuditEvidenceRecord>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        repository.ReadAllAsync(cancellationToken);
}
