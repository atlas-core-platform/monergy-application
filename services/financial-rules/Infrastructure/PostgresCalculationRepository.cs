using System.Collections.Immutable;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Npgsql;

namespace Monergy.Services.FinancialRules.Infrastructure;

public sealed class PostgresCalculationRepository : ICalculationRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresCalculationRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:FinancialRules:RuntimeConnection"));
    }

    public string AdapterKind => "POSTGRESQL_DURABLE";

    public CalculationExecution? FindRequest(CalculationRequestIdentity identity, string fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        var row = connection.QuerySingleOrDefault<OperationRow>("""
            SELECT i.request_fingerprint AS Fingerprint,
                   c.calculation_id AS CalculationId, c.customer_id AS CustomerId,
                   c.inputs::text AS Inputs, c.result::text AS Result, c.error::text AS Error,
                   c.outcome_event::text AS OutcomeEvent
            FROM financial_rules.idempotency_operations i
            JOIN financial_rules.calculations c ON c.calculation_id=i.calculation_id
            WHERE i.contract_name=@ContractName AND i.contract_version=@Version
              AND i.customer_id=@CustomerId AND i.idempotency_key=@IdempotencyKey;
            """, identity);
        if (row is null) return null;
        if (!string.Equals(row.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new CalculationException("calculation.idempotency.conflict", ContractErrorCategory.Conflict);
        return Deserialize(row);
    }

    public CalculationExecution? FindResult(string calculationId, string customerId)
    {
        using var connection = dataSource.OpenConnection();
        var row = connection.QuerySingleOrDefault<OperationRow>("""
            SELECT request_fingerprint AS Fingerprint, calculation_id AS CalculationId,
                   customer_id AS CustomerId, inputs::text AS Inputs, result::text AS Result,
                   error::text AS Error, outcome_event::text AS OutcomeEvent
            FROM financial_rules.calculations
            WHERE calculation_id=@calculationId AND customer_id=@customerId;
            """, new { calculationId, customerId });
        return row is null ? null : Deserialize(row);
    }

    public CalculationExecution Commit(CalculationRequestIdentity identity, string fingerprint,
        Func<CalculationExecution> applicationTransition)
    {
        ArgumentNullException.ThrowIfNull(applicationTransition);
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute("SELECT pg_advisory_xact_lock(hashtextextended(@Key, 0));",
            new { Key = string.Join('|', identity.ContractName, identity.Version, identity.CustomerId, identity.IdempotencyKey) }, transaction);
        var prior = connection.QuerySingleOrDefault<OperationRow>("""
            SELECT i.request_fingerprint AS Fingerprint, c.calculation_id AS CalculationId,
                   c.customer_id AS CustomerId, c.inputs::text AS Inputs, c.result::text AS Result,
                   c.error::text AS Error, c.outcome_event::text AS OutcomeEvent
            FROM financial_rules.idempotency_operations i
            JOIN financial_rules.calculations c ON c.calculation_id=i.calculation_id
            WHERE i.contract_name=@ContractName AND i.contract_version=@Version
              AND i.customer_id=@CustomerId AND i.idempotency_key=@IdempotencyKey;
            """, identity, transaction);
        if (prior is not null)
        {
            if (prior.Fingerprint != fingerprint)
                throw new CalculationException("calculation.idempotency.conflict", ContractErrorCategory.Conflict);
            transaction.Commit();
            return Deserialize(prior);
        }

        var execution = applicationTransition();
        var payload = execution.Event.Payload;
        connection.Execute("""
            INSERT INTO financial_rules.calculations
                (calculation_id, customer_id, rule_id, rule_version, request_fingerprint,
                 inputs, result, error, outcome_event, occurred_at)
            VALUES (@CalculationId, @CustomerId, @RuleId, @RuleVersion, @Fingerprint,
                    CAST(@Inputs AS jsonb), CAST(@Result AS jsonb), CAST(@Error AS jsonb),
                    CAST(@OutcomeEvent AS jsonb), @OccurredAt);
            """, new
        {
            execution.CalculationId,
            execution.CustomerId,
            payload.RuleId,
            payload.RuleVersion,
            Fingerprint = fingerprint,
            Inputs = JsonSerializer.Serialize(execution.Inputs, ContractJson.Options),
            Result = execution.Result is null ? null : JsonSerializer.Serialize(execution.Result, ContractJson.Options),
            Error = execution.Error is null ? null : JsonSerializer.Serialize(execution.Error, ContractJson.Options),
            OutcomeEvent = JsonSerializer.Serialize(execution.Event, ContractJson.Options),
            execution.Event.OccurredAt
        }, transaction);
        connection.Execute("""
            INSERT INTO financial_rules.idempotency_operations
                (contract_name, contract_version, customer_id, idempotency_key,
                 request_fingerprint, calculation_id, created_at)
            VALUES (@ContractName, @Version, @CustomerId, @IdempotencyKey,
                    @Fingerprint, @CalculationId, @CreatedAt);
            """, new
        {
            identity.ContractName,
            identity.Version,
            identity.CustomerId,
            identity.IdempotencyKey,
            Fingerprint = fingerprint,
            execution.CalculationId,
            CreatedAt = execution.Event.OccurredAt
        }, transaction);
        connection.Execute("""
            INSERT INTO financial_rules.outbox
                (event_id, contract_id, event_name, event_version, occurred_at, correlation_id,
                 causation_id, producer, subject_type, subject_id, payload, created_at)
            VALUES (@EventId, @ContractId, @EventName, @EventVersion, @OccurredAt, @CorrelationId,
                    @CausationId, @Producer, @SubjectType, @SubjectId, CAST(@Payload AS jsonb), @CreatedAt);
            """, new
        {
            execution.Event.EventId,
            execution.Event.ContractId,
            execution.Event.EventName,
            execution.Event.EventVersion,
            execution.Event.OccurredAt,
            execution.Event.CorrelationId,
            execution.Event.CausationId,
            execution.Event.Producer,
            execution.Event.SubjectType,
            execution.Event.SubjectId,
            Payload = JsonSerializer.Serialize(execution.Event.Payload, ContractJson.Options),
            CreatedAt = execution.Event.OccurredAt
        }, transaction);
        transaction.Commit();
        return execution;
    }

    public ImmutableArray<DomainEvent<CalculationOutcomePayload>> PendingEvents()
    {
        using var connection = dataSource.OpenConnection();
        return connection.Query<string>("""
            SELECT c.outcome_event::text FROM financial_rules.outbox o
            JOIN financial_rules.calculations c ON c.outcome_event->>'eventId'=o.event_id
            WHERE o.dispatched_at IS NULL ORDER BY o.created_at, o.event_id;
            """).Select(value => JsonSerializer.Deserialize<DomainEvent<CalculationOutcomePayload>>(value, ContractJson.Options)!)
            .ToImmutableArray();
    }

    public void Acknowledge(string eventId)
    {
        using var connection = dataSource.OpenConnection();
        connection.Execute("UPDATE financial_rules.outbox SET dispatched_at=now() WHERE event_id=@eventId;", new { eventId });
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static CalculationExecution Deserialize(OperationRow row) => new(row.CalculationId, row.CustomerId,
        JsonSerializer.Deserialize<ImmutableArray<CalculationInputLineage>>(row.Inputs, ContractJson.Options),
        row.Result is null ? null : JsonSerializer.Deserialize<CalculationResult>(row.Result, ContractJson.Options),
        row.Error is null ? null : JsonSerializer.Deserialize<ContractError>(row.Error, ContractJson.Options),
        JsonSerializer.Deserialize<DomainEvent<CalculationOutcomePayload>>(row.OutcomeEvent, ContractJson.Options)!);

    private sealed record OperationRow(string Fingerprint, string CalculationId, string CustomerId,
        string Inputs, string? Result, string? Error, string OutcomeEvent);
}
