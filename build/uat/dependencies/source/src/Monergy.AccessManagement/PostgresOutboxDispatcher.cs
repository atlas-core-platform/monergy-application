using System.Text.Json;
using Dapper;
using Npgsql;

namespace Monergy.AccessManagement;

public sealed class PostgresOutboxDispatcher(ITenantConnectionFactory connections, IAccessEventTransport transport)
{
    public const int MaximumAttempts = 5;

    public async Task<DeliverySummary> DispatchAsync(string tenantId, int limit, CancellationToken ct)
    {
        AccessInput.Identifier(tenantId, 64);
        AccessInput.Require(limit is >= 1 and <= 100, "INVALID_DELIVERY_LIMIT");
        await MaterializeAsync(tenantId, ct).ConfigureAwait(false);
        var delivered = 0;
        var retrying = 0;
        var dead = 0;
        var lost = 0;
        for (var index = 0; index < limit; index++)
        {
            ct.ThrowIfCancellationRequested();
            var lease = await ClaimAsync(tenantId, ct).ConfigureAwait(false);
            if (lease is null) break;
            try
            {
                if (lease.CycleAttempts > MaximumAttempts) throw new EventDeliveryException("ATTEMPTS_EXHAUSTED", true);
                var message = new AccessEventEnvelope(AccessEventProtocol.Contract(lease.EventName), lease.EventId, lease.EventName,
                    AccessEventProtocol.Version, new DateTimeOffset(DateTime.SpecifyKind(lease.OccurredAt, DateTimeKind.Utc)),
                    lease.CorrelationId, AccessEventProtocol.Producer, lease.TenantId, lease.AggregateId, lease.InitiatorActorId,
                    lease.PolicyVersion, lease.SubjectVersion, lease.Operation,
                    lease.EventName == "TenantUserAccessChanged" ? lease.AggregateId : null);
                AccessEventProtocol.Validate(message, tenantId, lease.Destination);
                var receipt = await transport.PublishAsync(lease.Destination, message, ct).ConfigureAwait(false);
                AccessEventProtocol.ValidateReceipt(receipt, message, lease.Destination);
                if (await CompleteAsync(tenantId, lease, receipt, ct).ConfigureAwait(false)) delivered++;
                else lost++;
            }
            catch (EventDeliveryException exception)
            {
                var terminal = exception.Permanent || lease.CycleAttempts >= MaximumAttempts;
                if (!await FailAsync(tenantId, lease, exception.Code, terminal, ct).ConfigureAwait(false)) lost++;
                else if (terminal) dead++;
                else retrying++;
            }
        }
        return new(delivered, retrying, dead, lost);
    }

    public async Task<IReadOnlyList<DeliveryStatus>> StatusAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        return (await connection.QueryAsync<DeliveryStatus>(new CommandDefinition(
            """
            SELECT status AS Status,count(*) AS Count FROM am.outbox_delivery GROUP BY status
            UNION ALL SELECT 'UnroutedEvents',count(*) FROM am.outbox o WHERE
                (SELECT count(*) FROM am.outbox_delivery d WHERE d.event_id=o.event_id) <
                CASE WHEN o.event_type='TenantUserAccessChanged' THEN 2 ELSE 1 END;
            """,
            cancellationToken: ct)).ConfigureAwait(false)).ToArray();
    }

    // This is an operator/worker port, deliberately absent from tenant HTTP APIs.
    // Mandatory append-only redrive evidence and the state change share a transaction.
    public async Task<bool> RedriveAsync(string tenantId, DeliveryRedrive request, CancellationToken ct)
    {
        AccessInput.Identifier(request.OperatorReference);
        AccessInput.Require(request.OperationId != Guid.Empty && request.EventId != Guid.Empty && request.ExpectedAttempts > 0 &&
            request.Destination is "authorization" or "sessions" or "audit" or "unroutable", "INVALID_REDRIVE");
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Lock the operation ID as well as the delivery row, making duplicate
        // operator requests idempotent across processes and conflicting payloads explicit.
        await connection.ExecuteAsync(Command("SELECT pg_advisory_xact_lock(hashtextextended(@Operation,0));",
            new { Operation = request.OperationId.ToString() }, transaction, ct)).ConfigureAwait(false);
        var prior = await connection.QuerySingleOrDefaultAsync<DeliveryRedrive>(Command("""
            SELECT operation_id AS OperationId,event_id AS EventId,destination AS Destination,
                expected_attempts AS ExpectedAttempts,operator_reference AS OperatorReference
            FROM am.outbox_redrives WHERE operation_id=@OperationId;
            """, request, transaction, ct)).ConfigureAwait(false);
        if (prior is not null)
        {
            AccessInput.Require(prior == request, "REDRIVE_ID_CONFLICT", 409);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }
        var changed = await connection.ExecuteAsync(Command("""
            UPDATE am.outbox_delivery SET status='Pending',cycle_attempts=0,redrives=redrives+1,
                next_attempt_at=clock_timestamp(),last_error_code=NULL
            WHERE event_id=@EventId AND destination=@Destination AND status='DeadLetter' AND attempts=@ExpectedAttempts;
            """, request, transaction, ct)).ConfigureAwait(false);
        AccessInput.Require(changed == 1, "REDRIVE_STATE_CONFLICT", 409);
        await connection.ExecuteAsync(Command("""
            INSERT INTO am.outbox_redrives(operation_id,event_id,destination,expected_attempts,operator_reference)
            VALUES (@OperationId,@EventId,@Destination,@ExpectedAttempts,@OperatorReference);
            """, request, transaction, ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task MaterializeAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO am.outbox_delivery(event_id,destination)
            SELECT o.event_id,r.destination FROM am.outbox o
            CROSS JOIN LATERAL (
                SELECT 'authorization' AS destination WHERE o.event_type IN ('AuthorizationPolicyChanged','TenantUserAccessChanged')
                UNION ALL SELECT 'sessions' WHERE o.event_type='TenantUserAccessChanged'
                UNION ALL SELECT 'audit' WHERE o.event_type='AccessManagementAuditEvidence'
                UNION ALL SELECT 'unroutable' WHERE o.event_type NOT IN ('AuthorizationPolicyChanged','TenantUserAccessChanged','AccessManagementAuditEvidence')
            ) r
            WHERE NOT EXISTS (SELECT 1 FROM am.outbox_delivery d WHERE d.event_id=o.event_id AND d.destination=r.destination)
            ORDER BY o.occurred_at,o.event_id,r.destination LIMIT 200
            ON CONFLICT (event_id,destination) DO NOTHING;
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<DeliveryLease?> ClaimAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DeliveryLease>(new CommandDefinition("""
            WITH candidate AS (
                SELECT event_id,destination FROM am.outbox_delivery
                WHERE (status='Pending' AND next_attempt_at<=clock_timestamp()) OR
                    (status='InFlight' AND lease_until<=clock_timestamp())
                ORDER BY next_attempt_at,event_id,destination FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
                UPDATE am.outbox_delivery d SET status='InFlight',attempts=attempts+1,cycle_attempts=cycle_attempts+1,
                    lease_token=@Token,lease_until=clock_timestamp()+interval '30 seconds'
                FROM candidate c WHERE d.event_id=c.event_id AND d.destination=c.destination
                RETURNING d.event_id,d.destination,d.lease_token,d.cycle_attempts
            )
            SELECT c.event_id AS EventId,c.destination AS Destination,c.lease_token AS LeaseToken,c.cycle_attempts AS CycleAttempts,
                o.event_type AS EventName,o.tenant_id AS TenantId,o.aggregate_id AS AggregateId,o.initiator_actor_id AS InitiatorActorId,
                o.correlation_id AS CorrelationId,o.policy_version AS PolicyVersion,o.subject_version AS SubjectVersion,
                o.payload->>'operation' AS Operation,o.occurred_at AS OccurredAt
            FROM claimed c JOIN am.outbox o ON o.event_id=c.event_id;
            """, new { Token = Guid.NewGuid() }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<bool> CompleteAsync(string tenantId, DeliveryLease lease, AccessEventReceipt receipt, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE am.outbox_delivery SET status='Delivered',lease_token=NULL,lease_until=NULL,
                delivered_at=clock_timestamp(),receipt=CAST(@Receipt AS jsonb),last_error_code=NULL
            WHERE event_id=@EventId AND destination=@Destination AND status='InFlight'
                AND lease_token=@LeaseToken AND lease_until>clock_timestamp();
            """, new { lease.EventId, lease.Destination, lease.LeaseToken, Receipt = JsonSerializer.Serialize(receipt, AccessEventProtocol.Json) },
            cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    private async Task<bool> FailAsync(string tenantId, DeliveryLease lease, string code, bool terminal, CancellationToken ct)
    {
        // Provider diagnostics never enter persisted failure fields.
        var safeCode = code is "UNSUPPORTED_EVENT" or "INVALID_EVENT" or "INVALID_RECEIPT" or "HTTP_DELIVERY_REJECTED" or
            "TRANSPORT_UNAVAILABLE" or "TRANSPORT_TIMEOUT" or "ATTEMPTS_EXHAUSTED" ? code : "TRANSPORT_UNAVAILABLE";
        var baseDelay = Math.Min(300, 1 << Math.Min(lease.CycleAttempts, 8));
        var jitter = lease.EventId.ToByteArray()[0] % (baseDelay + 1);
        await using var connection = await connections.OpenAsync(tenantId, ct).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE am.outbox_delivery SET status=@Status,lease_token=NULL,lease_until=NULL,last_error_code=@Code,
                next_attempt_at=clock_timestamp()+make_interval(secs => @Delay)
            WHERE event_id=@EventId AND destination=@Destination AND status='InFlight'
                AND lease_token=@LeaseToken AND lease_until>clock_timestamp();
            """, new { lease.EventId, lease.Destination, lease.LeaseToken, Status = terminal ? "DeadLetter" : "Pending",
                Code = safeCode, Delay = Math.Min(300, baseDelay + jitter) }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    private static CommandDefinition Command(string sql, object parameters, NpgsqlTransaction transaction, CancellationToken ct) =>
        new(sql, parameters, transaction, cancellationToken: ct);

    private sealed class DeliveryLease
    {
        public Guid EventId { get; set; }
        public string Destination { get; set; } = "";
        public Guid LeaseToken { get; set; }
        public int CycleAttempts { get; set; }
        public string EventName { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string AggregateId { get; set; } = "";
        public string InitiatorActorId { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public long PolicyVersion { get; set; }
        public long? SubjectVersion { get; set; }
        public string Operation { get; set; } = "";
        public DateTime OccurredAt { get; set; }
    }
}
