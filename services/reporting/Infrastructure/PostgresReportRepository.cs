using System.Collections.Immutable;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Npgsql;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class PostgresReportRepository : IReportRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresReportRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:Reporting:RuntimeConnection"));
    }

    public async Task<ReportOperationResult> GetOrCreateAsync(ReportOperationIdentity identity,
        Func<CancellationToken, Task<ReportGenerationAttempt>> reportFactory,
        Func<TrustedFinancialReport, DomainEvent<ReportGeneratedPayload>> eventFactory,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0));",
            new { Key = string.Join('|', identity.ContractName, identity.ContractVersion, identity.CustomerId, identity.IdempotencyKey) },
            transaction, cancellationToken: cancellationToken));
        var priorId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT report_id FROM reporting.idempotency_operations
            WHERE contract_name=@ContractName AND contract_version=@ContractVersion
              AND customer_id=@CustomerId AND idempotency_key=@IdempotencyKey;
            """, identity, transaction, cancellationToken: cancellationToken));
        if (priorId is not null)
        {
            var replay = await FindAsync(connection, transaction, identity.CustomerId, priorId, cancellationToken)
                ?? throw new InvalidOperationException("Committed report operation is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            return new(replay, null, false);
        }

        var attempt = await reportFactory(cancellationToken);
        if (attempt.Report is null || attempt.Error is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, attempt.Error, false);
        }

        var report = attempt.Report;
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reporting.reports
                (report_id, customer_id, state, generated_at, items, source_financial_references,
                 evidence_references, financial_provenance_references, calculation_lineage_references,
                 ai_response_trace_reference, audit_compatibility_reference_id, export_file_name,
                 export_media_type, export_content, export_sha256)
            VALUES (@ReportId, @CustomerId, @State, @GeneratedAt, CAST(@Items AS jsonb),
                    CAST(@SourceRefs AS jsonb), CAST(@EvidenceRefs AS jsonb), CAST(@ProvenanceRefs AS jsonb),
                    CAST(@LineageRefs AS jsonb), @AiResponseTraceReference, @AuditCompatibilityReferenceId,
                    @FileName, @MediaType, @Content, @Sha256);
            """, new
        {
            report.ReportId,
            report.CustomerId,
            report.State,
            report.GeneratedAt,
            Items = JsonSerializer.Serialize(report.Items, ContractJson.Options),
            SourceRefs = JsonSerializer.Serialize(report.SourceFinancialReferences, ContractJson.Options),
            EvidenceRefs = JsonSerializer.Serialize(report.EvidenceReferences, ContractJson.Options),
            ProvenanceRefs = JsonSerializer.Serialize(report.FinancialProvenanceReferences, ContractJson.Options),
            LineageRefs = JsonSerializer.Serialize(report.CalculationLineageReferences, ContractJson.Options),
            report.AiResponseTraceReference,
            report.AuditCompatibilityReferenceId,
            report.Export.FileName,
            report.Export.MediaType,
            report.Export.Content,
            report.Export.Sha256
        },
            transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reporting.idempotency_operations
                (contract_name, contract_version, customer_id, idempotency_key, report_id, created_at)
            VALUES (@ContractName, @ContractVersion, @CustomerId, @IdempotencyKey, @ReportId, @CreatedAt);
            """, new
        {
            identity.ContractName,
            identity.ContractVersion,
            identity.CustomerId,
            identity.IdempotencyKey,
            report.ReportId,
            CreatedAt = report.GeneratedAt
        }, transaction,
            cancellationToken: cancellationToken));
        var governedEvent = eventFactory(report);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reporting.outbox
                (event_id, contract_id, event_name, event_version, occurred_at, correlation_id,
                 causation_id, producer, subject_type, subject_id, payload, created_at)
            VALUES (@EventId, @ContractId, @EventName, @EventVersion, @OccurredAt, @CorrelationId,
                    @CausationId, @Producer, @SubjectType, @SubjectId, CAST(@Payload AS jsonb), @CreatedAt);
            """, new
        {
            governedEvent.EventId,
            governedEvent.ContractId,
            governedEvent.EventName,
            governedEvent.EventVersion,
            governedEvent.OccurredAt,
            governedEvent.CorrelationId,
            governedEvent.CausationId,
            governedEvent.Producer,
            governedEvent.SubjectType,
            governedEvent.SubjectId,
            Payload = JsonSerializer.Serialize(governedEvent.Payload, ContractJson.Options),
            CreatedAt = report.GeneratedAt
        }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        var committed = await FindAsync(connection, null, report.CustomerId, report.ReportId, cancellationToken)
            ?? throw new InvalidOperationException("Committed report is unavailable after transaction completion.");
        return new(committed, null, true);
    }

    public TrustedFinancialReport? Find(string customerId, string reportId)
    {
        using var connection = dataSource.OpenConnection();
        return FindAsync(connection, null, customerId, reportId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public IReadOnlyList<DomainEvent<ReportGeneratedPayload>> PendingEvents()
    {
        using var connection = dataSource.OpenConnection();
        var rows = connection.Query<OutboxRow>("""
            SELECT event_id AS EventId, contract_id AS ContractId, event_name AS EventName,
                   event_version AS EventVersion, occurred_at AS OccurredAt,
                   correlation_id AS CorrelationId, causation_id AS CausationId, producer AS Producer,
                   subject_type AS SubjectType, subject_id AS SubjectId, payload::text AS Payload
            FROM reporting.outbox WHERE dispatched_at IS NULL ORDER BY created_at, event_id;
            """);
        return rows.Select(row => new DomainEvent<ReportGeneratedPayload>(row.ContractId, row.EventId,
            row.EventName, row.EventVersion,
            new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)),
            row.CorrelationId, row.CausationId,
            row.Producer, row.SubjectType, row.SubjectId,
            JsonSerializer.Deserialize<ReportGeneratedPayload>(row.Payload, ContractJson.Options)!)).ToArray();
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static async Task<TrustedFinancialReport?> FindAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string customerId, string reportId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReportRow>(new CommandDefinition("""
            SELECT report_id AS ReportId, customer_id AS CustomerId, state AS State,
                   generated_at AS GeneratedAt, items::text AS Items,
                   source_financial_references::text AS SourceRefs,
                   evidence_references::text AS EvidenceRefs,
                   financial_provenance_references::text AS ProvenanceRefs,
                   calculation_lineage_references::text AS LineageRefs,
                   ai_response_trace_reference AS AiResponseTraceReference,
                   audit_compatibility_reference_id AS AuditCompatibilityReferenceId,
                   export_file_name AS FileName, export_media_type AS MediaType,
                   export_content AS Content, export_sha256 AS Sha256
            FROM reporting.reports WHERE customer_id=@customerId AND report_id=@reportId;
            """, new { customerId, reportId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : new(row.ReportId, row.CustomerId, row.State,
            new DateTimeOffset(DateTime.SpecifyKind(row.GeneratedAt, DateTimeKind.Utc)),
            JsonSerializer.Deserialize<ImmutableArray<ReportLineItem>>(row.Items, ContractJson.Options),
            Array(row.SourceRefs), Array(row.EvidenceRefs), Array(row.ProvenanceRefs), Array(row.LineageRefs),
            row.AiResponseTraceReference, row.AuditCompatibilityReferenceId,
            new(row.FileName, row.MediaType, row.Content, row.Sha256));
    }

    private static ImmutableArray<string> Array(string json) =>
        JsonSerializer.Deserialize<ImmutableArray<string>>(json, ContractJson.Options);

    private sealed class ReportRow
    {
        public string ReportId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public string Items { get; set; } = string.Empty;
        public string SourceRefs { get; set; } = string.Empty;
        public string EvidenceRefs { get; set; } = string.Empty;
        public string ProvenanceRefs { get; set; } = string.Empty;
        public string LineageRefs { get; set; } = string.Empty;
        public string? AiResponseTraceReference { get; set; }
        public string AuditCompatibilityReferenceId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }
    private sealed class OutboxRow
    {
        public string EventId { get; set; } = string.Empty;
        public string ContractId { get; set; } = string.Empty;
        public string EventName { get; set; } = string.Empty;
        public string EventVersion { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string? CausationId { get; set; }
        public string Producer { get; set; } = string.Empty;
        public string SubjectType { get; set; } = string.Empty;
        public string SubjectId { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }
}
