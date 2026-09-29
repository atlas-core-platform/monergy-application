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
    string AdapterKind { get; }

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
        new(StringComparer.Ordinal) { "CID-023", "CID-024", "CID-028", "CID-034", "CID-035", "CID-040", "CID-041" };

    public async Task<AuditEvidenceRecord> ConsumeAsync(
        AuditableEvent source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ContractId is "CID-040" or "CID-041" &&
            (source.Producer != "Financial Rules Service" || source.EventVersion != ContractGuard.CurrentVersion ||
             source.SubjectType != "financial-calculation" ||
             source.EventName != (source.ContractId == "CID-040" ? FinancialRulesContractNames.CalculationCompleted : FinancialRulesContractNames.CalculationFailed)))
        {
            throw new ArgumentException("The calculation event producer, type or version is invalid.", nameof(source));
        }
        if (!AcceptedContracts.Contains(source.ContractId) ||
            string.IsNullOrWhiteSpace(source.EventId) ||
            string.IsNullOrWhiteSpace(source.CorrelationId) ||
            string.IsNullOrWhiteSpace(source.SubjectId))
        {
            throw new ArgumentException("The source event is not an auditable governed fact.", nameof(source));
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
            repository.AdapterKind));
        return appended.Record;
    }

    public Task<IReadOnlyList<AuditEvidenceRecord>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        repository.ReadAllAsync(cancellationToken);

    // Typed governed consumer. Transport must establish trusted producer identity separately in a durable adapter.
    public Task<AuditEvidenceRecord> ConsumeCalculationAsync(
        DomainEvent<CalculationOutcomePayload> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var payload = source.Payload;
        if (source.ContractId is not ("CID-040" or "CID-041") || payload is null ||
            string.IsNullOrWhiteSpace(payload.CustomerId) || string.IsNullOrWhiteSpace(payload.CalculationResultId) ||
            string.IsNullOrWhiteSpace(payload.CalculationLineageId) || payload.CalculationResultId != source.SubjectId ||
            string.IsNullOrWhiteSpace(payload.RuleId) || string.IsNullOrWhiteSpace(payload.RuleVersion) ||
            payload.Revision != 1 || source.OccurredAt == default || string.IsNullOrWhiteSpace(source.CausationId) ||
            (source.ContractId == "CID-040" ? payload.FailureCode is not null : string.IsNullOrWhiteSpace(payload.FailureCode)))
        {
            throw new ArgumentException("Calculation outcome lineage is invalid.", nameof(source));
        }

        return ConsumeAsync(new AuditableEvent(source.ContractId, source.EventId, source.EventName, source.EventVersion,
            source.OccurredAt, source.CorrelationId, source.CausationId, source.Producer, source.SubjectType, source.SubjectId), cancellationToken);
    }
}
