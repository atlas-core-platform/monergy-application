using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Npgsql;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed partial class PostgresDocumentClassificationRepository :
    IDocumentProcessingRepository,
    IClassificationMonitoringRepository
{
    private static ProcessingRecord DecodeProcessing(string json) =>
        JsonSerializer.Deserialize<ProcessingRecord>(json)
        ?? throw new InvalidOperationException("Invalid document processing record.");

    private static void VerifyProcessing(ProcessingRecord record, string tenant, string processingId)
    {
        if (record.TenantId != tenant || record.ProcessingId != processingId)
            throw new InvalidOperationException("Document processing identity mismatch.");
    }

    private static async Task<ProcessingRecord?> ReadProcessingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenant,
        string processingId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT record::text FROM document_processing " +
            "WHERE tenant_id=@tenant AND processing_id=@processing" +
            (forUpdate ? " FOR UPDATE" : string.Empty);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("processing", processingId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null) return null;
        var record = DecodeProcessing(json);
        VerifyProcessing(record, tenant, processingId);
        return record;
    }

    private async Task<(string Tenant, ProcessingRecord Record)?> FindProcessingAsync(
        string processingId,
        CancellationToken cancellationToken)
    {
        (string Tenant, ProcessingRecord Record)? found = null;
        foreach (var source in sources)
        {
            await using var connection = await source.Value.OpenConnectionAsync(cancellationToken);
            var record = await ReadProcessingAsync(
                connection, null, source.Key, processingId, false, cancellationToken);
            if (record is null) continue;
            if (found is not null)
                throw new ProcessingConflictException("Processing identity is not tenant-unique.");
            found = (source.Key, record);
        }
        return found;
    }

    public async Task<(ProcessingRecord Record, bool Created)> AcceptAsync(
        ProcessingRecord candidate,
        CancellationToken cancellationToken)
    {
        var tenant = candidate.TenantId ?? throw new ProcessingConflictException(
            "Physical processing requires a tenant identity.");
        if (!TenantAccessClient.Identifier(tenant, 64))
            throw new ProcessingConflictException("Physical processing requires a tenant identity.");

        await using var connection = await Source(tenant).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var replay = new NpgsqlCommand(
            "SELECT record::text FROM document_processing WHERE tenant_id=@tenant " +
            "AND contract_name=@contract AND contract_version=@version " +
            "AND customer_id=@customer AND idempotency_key=@idempotency FOR UPDATE",
            connection, transaction))
        {
            replay.Parameters.AddWithValue("tenant", tenant);
            replay.Parameters.AddWithValue("contract", candidate.ContractName);
            replay.Parameters.AddWithValue("version", candidate.ContractVersion);
            replay.Parameters.AddWithValue("customer", candidate.CustomerId);
            replay.Parameters.AddWithValue("idempotency", candidate.IdempotencyKey);
            if (await replay.ExecuteScalarAsync(cancellationToken) is string existingJson)
            {
                var existing = DecodeProcessing(existingJson);
                if (existing.TenantId != tenant ||
                    !string.Equals(existing.PayloadFingerprint, candidate.PayloadFingerprint, StringComparison.Ordinal))
                    throw new ProcessingConflictException("Idempotency key conflict.");
                await transaction.CommitAsync(cancellationToken);
                return (existing, false);
            }
        }

        if (await ReadProcessingAsync(
                connection, transaction, tenant, candidate.ProcessingId, true, cancellationToken) is not null)
            throw new ProcessingConflictException("Processing identity exists.");

        if (candidate.PredecessorSnapshot is not null)
        {
            var predecessor = await ReadProcessingAsync(
                connection,
                transaction,
                tenant,
                candidate.PreviousProcessingId!,
                true,
                cancellationToken);
            if (predecessor is null || !MatchesSnapshot(predecessor, candidate.PredecessorSnapshot))
                throw new ProcessingConflictException("The predecessor changed before reprocessing admission.");
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO document_processing(" +
            "tenant_id,processing_id,contract_name,contract_version,customer_id,idempotency_key,state,updated_at,record) " +
            "VALUES (@tenant,@processing,@contract,@version,@customer,@idempotency,@state,@updated,@record::jsonb)",
            connection, transaction))
        {
            AddProcessingParameters(insert, candidate);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ProcessingAtomicCommitException("Processing admission was not committed.");
        }
        await transaction.CommitAsync(cancellationToken);
        return (candidate, true);
    }

    public async Task<ProcessingRecord?> GetByIdempotencyAsync(
        string contractName,
        string contractVersion,
        string customerId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ProcessingRecord? found = null;
        foreach (var source in sources)
        {
            await using var command = source.Value.CreateCommand(
                "SELECT record::text FROM document_processing WHERE tenant_id=@tenant " +
                "AND contract_name=@contract AND contract_version=@version " +
                "AND customer_id=@customer AND idempotency_key=@idempotency");
            command.Parameters.AddWithValue("tenant", source.Key);
            command.Parameters.AddWithValue("contract", contractName);
            command.Parameters.AddWithValue("version", contractVersion);
            command.Parameters.AddWithValue("customer", customerId);
            command.Parameters.AddWithValue("idempotency", idempotencyKey);
            if (await command.ExecuteScalarAsync(cancellationToken) is not string json) continue;
            var current = DecodeProcessing(json);
            if (current.TenantId != source.Key)
                throw new InvalidOperationException("Document processing tenant mismatch.");
            if (found is not null)
                throw new ProcessingConflictException("Idempotency identity is not tenant-unique.");
            found = current;
        }
        return found;
    }

    public async Task<ProcessingRecord> StartAttemptAsync(
        string processingId,
        ProcessingState expected,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var located = await FindProcessingAsync(processingId, cancellationToken)
            ?? throw new ProcessingConflictException("Invalid processing attempt start.");
        await using var connection = await Source(located.Tenant).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadProcessingAsync(
            connection, transaction, located.Tenant, processingId, true, cancellationToken);
        if (current is null || current.State != expected ||
            expected is not (ProcessingState.Accepted or ProcessingState.Failed) ||
            (expected == ProcessingState.Failed && !current.Retryable))
            throw new ProcessingConflictException("Invalid processing attempt start.");

        var attempt = current.Attempt + 1;
        var next = current with
        {
            State = ProcessingState.Running,
            Attempt = attempt,
            ActiveAttemptId = $"{processingId}:attempt:{attempt}",
            UpdatedAt = startedAt,
            FailureCode = null,
            FailureCategory = null,
            Retryable = false,
        };
        await UpdateProcessingAsync(connection, transaction, next, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    public async Task<ProcessingRecord> CommitTerminalAsync(
        string processingId,
        string attemptId,
        ProcessingState target,
        DateTimeOffset updatedAt,
        IReadOnlyList<ValidatedSourceFact>? facts,
        string? failureCode,
        ContractErrorCategory? failureCategory,
        bool retryable,
        object outboxEvent,
        CancellationToken cancellationToken)
    {
        var located = await FindProcessingAsync(processingId, cancellationToken)
            ?? throw new ProcessingConflictException("Invalid or stale terminal processing attempt.");
        await using var connection = await Source(located.Tenant).OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadProcessingAsync(
            connection, transaction, located.Tenant, processingId, true, cancellationToken);
        if (current is null || current.State != ProcessingState.Running ||
            !string.Equals(current.ActiveAttemptId, attemptId, StringComparison.Ordinal) ||
            target is not (ProcessingState.Completed or ProcessingState.Failed))
            throw new ProcessingConflictException("Invalid or stale terminal processing attempt.");

        var eventId = EventId(outboxEvent);
        var next = current with
        {
            State = target,
            UpdatedAt = updatedAt,
            Facts = (facts ?? current.Facts).ToArray(),
            FailureCode = failureCode,
            FailureCategory = failureCategory,
            Retryable = retryable,
            ActiveAttemptId = null,
            TerminalEventId = eventId,
        };
        await UpdateProcessingAsync(connection, transaction, next, cancellationToken);
        await using (var insertEvent = new NpgsqlCommand(
            "INSERT INTO document_processing_events(tenant_id,event_id,processing_id,created_at,event) " +
            "VALUES (@tenant,@eventId,@processing,@created,@event::jsonb)",
            connection, transaction))
        {
            insertEvent.Parameters.AddWithValue("tenant", located.Tenant);
            insertEvent.Parameters.AddWithValue("eventId", eventId);
            insertEvent.Parameters.AddWithValue("processing", processingId);
            insertEvent.Parameters.AddWithValue("created", updatedAt);
            insertEvent.Parameters.AddWithValue("event", JsonSerializer.Serialize(outboxEvent, outboxEvent.GetType()));
            if (await insertEvent.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ProcessingAtomicCommitException("Processing terminal event was not committed.");
        }
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    public async Task<ProcessingRecord?> GetAsync(
        string processingId,
        CancellationToken cancellationToken) =>
        (await FindProcessingAsync(processingId, cancellationToken))?.Record;

    public async Task<ClassificationMonitorPage> MonitorAsync(
        string tenant,
        string customer,
        string version,
        string? after,
        CancellationToken ct)
    {
        await using var command = Source(tenant).CreateCommand(
            "SELECT record::text FROM document_processing " +
            "WHERE tenant_id=@tenant AND customer_id=@customer " +
            "AND record->>'DocumentVersionId'=@version " +
            (after is null ? string.Empty : "AND processing_id<@after ") +
            "ORDER BY processing_id DESC LIMIT 51");
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("customer", customer);
        command.Parameters.AddWithValue("version", version);
        if (after is not null) command.Parameters.AddWithValue("after", after);
        var records = new List<ProcessingRecord>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                records.Add(DecodeProcessing(reader.GetString(0)));
        }
        if (after is not null && records.Count == 0)
        {
            await using var cursor = Source(tenant).CreateCommand(
                "SELECT 1 FROM document_processing WHERE tenant_id=@tenant AND processing_id=@after " +
                "AND customer_id=@customer AND record->>'DocumentVersionId'=@version");
            cursor.Parameters.AddWithValue("tenant", tenant);
            cursor.Parameters.AddWithValue("after", after);
            cursor.Parameters.AddWithValue("customer", customer);
            cursor.Parameters.AddWithValue("version", version);
            if (await cursor.ExecuteScalarAsync(ct) is null)
                throw new TenantBoundaryException("CLASSIFICATION_CURSOR_INVALID", 400);
        }
        var page = records.Take(50).Select(record => new ClassificationMonitorItem(
            record.ProcessingId,
            record.State.ToString(),
            record.FailureCode is null ? null :
                TenantAccessClient.Identifier(record.FailureCode, 160) ? record.FailureCode : "processing.metadata.unavailable",
            record.Retryable,
            record.Attempt,
            record.UpdatedAt,
            (record.State == ProcessingState.Completed ? record.Facts : [])
                .Select(fact => fact.FactType.Split('_')[0])
                .Where(DocumentClassificationMonitoring.Categories.Contains)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray())).ToArray();
        return new ClassificationMonitorPage(
            tenant,
            customer,
            version,
            page,
            records.Count > 50 ? page[^1].ProcessingId : null);
    }

    private static async Task UpdateProcessingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingRecord record,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            "UPDATE document_processing SET state=@state,updated_at=@updated,record=@record::jsonb " +
            "WHERE tenant_id=@tenant AND processing_id=@processing",
            connection, transaction);
        update.Parameters.AddWithValue("state", record.State.ToString());
        update.Parameters.AddWithValue("updated", record.UpdatedAt);
        update.Parameters.AddWithValue("record", JsonSerializer.Serialize(record));
        update.Parameters.AddWithValue("tenant", record.TenantId!);
        update.Parameters.AddWithValue("processing", record.ProcessingId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ProcessingAtomicCommitException("Processing state was not committed.");
    }

    private static void AddProcessingParameters(NpgsqlCommand command, ProcessingRecord record)
    {
        command.Parameters.AddWithValue("tenant", record.TenantId!);
        command.Parameters.AddWithValue("processing", record.ProcessingId);
        command.Parameters.AddWithValue("contract", record.ContractName);
        command.Parameters.AddWithValue("version", record.ContractVersion);
        command.Parameters.AddWithValue("customer", record.CustomerId);
        command.Parameters.AddWithValue("idempotency", record.IdempotencyKey);
        command.Parameters.AddWithValue("state", record.State.ToString());
        command.Parameters.AddWithValue("updated", record.UpdatedAt);
        command.Parameters.AddWithValue("record", JsonSerializer.Serialize(record));
    }

    private static bool MatchesSnapshot(ProcessingRecord current, ProcessingPredecessorSnapshot expected) =>
        current.ProcessingId == expected.ProcessingId &&
        current.DocumentVersionId == expected.DocumentVersionId &&
        current.EvidenceReferenceId == expected.EvidenceReferenceId &&
        current.State == expected.State &&
        current.FailureCode == expected.FailureCode &&
        current.Retryable == expected.Retryable &&
        current.UpdatedAt == expected.UpdatedAt &&
        current.Facts.Count == expected.FactCount &&
        current.TerminalEventId == expected.TerminalEventId;

    private static string EventId(object value) => value switch
    {
        DomainEvent<ValidatedSourceFactsProducedPayload> success => success.EventId,
        DomainEvent<DocumentProcessingFailedPayload> failure => failure.EventId,
        _ => throw new InvalidOperationException("Unsupported processing terminal event type."),
    };
}
