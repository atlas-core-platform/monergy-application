using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Npgsql;

namespace Monergy.Services.Audit.Infrastructure;

public sealed class PostgresAuditEvidenceRepository : IAuditEvidenceRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresAuditEvidenceRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:Audit:RuntimeConnection"));
    }

    public string AdapterKind => "POSTGRESQL_DURABLE";

    public async Task<(AuditEvidenceRecord Record, bool Created)> AppendAsync(
        AuditableEvent source, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var candidate = new AuditEvidenceRecord($"audit-{source.EventId}", source.ContractId,
            source.EventId, source.EventName, source.Producer, source.SubjectType, source.SubjectId,
            source.OccurredAt, source.CorrelationId, source.CausationId, recordedAt);
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit.evidence
                (audit_evidence_id, source_contract_id, source_event_id, event_name, producer,
                 subject_type, subject_id, occurred_at, correlation_id, causation_id, recorded_at)
            VALUES
                (@AuditEvidenceId, @SourceContractId, @SourceEventId, @EventName, @Producer,
                 @SubjectType, @SubjectId, @OccurredAt, @CorrelationId, @CausationId, @RecordedAt)
            ON CONFLICT (source_event_id) DO NOTHING;
            """, candidate, cancellationToken: cancellationToken));
        var row = await connection.QuerySingleAsync<AuditRow>(new CommandDefinition("""
            SELECT audit_evidence_id AS AuditEvidenceId, source_contract_id AS SourceContractId,
                   source_event_id AS SourceEventId, event_name AS EventName, producer AS Producer,
                   subject_type AS SubjectType, subject_id AS SubjectId, occurred_at AS OccurredAt,
                   correlation_id AS CorrelationId, causation_id AS CausationId, recorded_at AS RecordedAt
            FROM audit.evidence WHERE source_event_id=@EventId;
            """, new { source.EventId }, cancellationToken: cancellationToken));
        return (ToRecord(row), inserted == 1);
    }

    public async Task<IReadOnlyList<AuditEvidenceRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var records = await connection.QueryAsync<AuditRow>(new CommandDefinition("""
            SELECT audit_evidence_id AS AuditEvidenceId, source_contract_id AS SourceContractId,
                   source_event_id AS SourceEventId, event_name AS EventName, producer AS Producer,
                   subject_type AS SubjectType, subject_id AS SubjectId, occurred_at AS OccurredAt,
                   correlation_id AS CorrelationId, causation_id AS CausationId, recorded_at AS RecordedAt
            FROM audit.evidence ORDER BY recorded_at, audit_evidence_id;
            """, cancellationToken: cancellationToken));
        return records.Select(ToRecord).ToArray();
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static AuditEvidenceRecord ToRecord(AuditRow row) => new(row.AuditEvidenceId,
        row.SourceContractId, row.SourceEventId, row.EventName, row.Producer, row.SubjectType,
        row.SubjectId, Utc(row.OccurredAt), row.CorrelationId, row.CausationId, Utc(row.RecordedAt));
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class AuditRow
    {
        public string AuditEvidenceId { get; set; } = string.Empty;
        public string SourceContractId { get; set; } = string.Empty;
        public string SourceEventId { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public string Producer { get; set; } = string.Empty;
        public string SubjectType { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string? CausationId { get; set; }
        public DateTime RecordedAt { get; set; }
    }
}
