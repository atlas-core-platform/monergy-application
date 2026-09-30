using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Npgsql;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed class PostgresJobRepository : IJobRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresJobRepository(IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        dataSource = NpgsqlDataSource.Create(PhysicalPersistenceGuard.Require(configuration,
            "Monergy:Persistence:JobManagement:RuntimeConnection"));
    }

    public string AdapterKind => "POSTGRESQL_DURABLE";

    public async Task<(DurableJobRecord Job, bool Created)> ScheduleAsync(JobSubmission submission,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var key = string.Join('|', submission.ContractName, submission.ContractVersion,
            submission.CustomerId, submission.IdempotencyKey);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", new { key }, transaction,
            cancellationToken: cancellationToken));
        var prior = await connection.QuerySingleOrDefaultAsync<IdempotencyRow>(new CommandDefinition("""
            SELECT job_id AS JobId, payload_fingerprint AS PayloadFingerprint
            FROM job_management.idempotency_operations
            WHERE contract_name=@ContractName AND contract_version=@ContractVersion
              AND customer_id=@CustomerId AND idempotency_key=@IdempotencyKey;
            """, submission, transaction, cancellationToken: cancellationToken));
        if (prior is not null)
        {
            if (!string.Equals(prior.PayloadFingerprint, submission.PayloadFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Idempotency key conflict.");
            var replay = await FindAsync(connection, transaction, prior.JobId, cancellationToken)
                ?? throw new InvalidOperationException("Committed job operation is incomplete.");
            await transaction.CommitAsync(cancellationToken);
            return (replay, false);
        }

        var job = new DurableJobRecord(submission.JobId, submission.JobName, submission.ContractVersion,
            submission.OwnerService, submission.PayloadReference, submission.CustomerId, submission.RequestId,
            submission.CorrelationId, submission.CausationId, submission.Security, submission.IdempotencyKey,
            submission.PayloadFingerprint, JobState.Scheduled, 0, 0, 0, submission.RequestedAt,
            submission.ScheduledAt, submission.ScheduledAt, null, null, null, false, null,
            null, null, null, false,
            submission.ScheduledAt);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO job_management.jobs
                    (job_id, job_name, job_version, owner_service, payload_reference, customer_id,
                     request_id, correlation_id, causation_id, security_context, idempotency_key,
                     payload_fingerprint, state, attempt, replay_count, requested_at, scheduled_at,
                     next_attempt_at, retryable, updated_at)
                VALUES
                    (@JobId, @JobName, @JobVersion, @OwnerService, @PayloadReference, @CustomerId,
                     @RequestId, @CorrelationId, @CausationId, CAST(@SecurityJson AS jsonb), @IdempotencyKey,
                     @PayloadFingerprint, @State, 0, 0, @RequestedAt, @ScheduledAt, @NextAttemptAt,
                     false, @UpdatedAt);
                """, new
            {
                job.JobId,
                job.JobName,
                job.JobVersion,
                job.OwnerService,
                job.PayloadReference,
                job.CustomerId,
                job.RequestId,
                job.CorrelationId,
                job.CausationId,
                SecurityJson = JsonSerializer.Serialize(job.Security, ContractJson.Options),
                job.IdempotencyKey,
                job.PayloadFingerprint,
                State = job.State.ToString(),
                job.RequestedAt,
                job.ScheduledAt,
                job.NextAttemptAt,
                job.UpdatedAt
            }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO job_management.idempotency_operations
                    (contract_name, contract_version, customer_id, idempotency_key,
                     payload_fingerprint, job_id, created_at)
                VALUES (@ContractName, @ContractVersion, @CustomerId, @IdempotencyKey,
                        @PayloadFingerprint, @JobId, @ScheduledAt);
                """, new
            {
                submission.ContractName,
                submission.ContractVersion,
                submission.CustomerId,
                submission.IdempotencyKey,
                submission.PayloadFingerprint,
                submission.JobId,
                submission.ScheduledAt
            }, transaction, cancellationToken: cancellationToken));
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Job identity exists.", error);
        }
        await transaction.CommitAsync(cancellationToken);
        return (job, true);
    }

    public async Task<DurableJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, null, jobId, cancellationToken);
    }

    public async Task<JobLease?> ClaimAsync(string jobId, string workerId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await FindAsync(connection, transaction, jobId, cancellationToken, true);
        if (current is null || !IsEligible(current, now))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var lease = await ClaimLockedAsync(connection, transaction, current, workerId, now, leaseDuration,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    public async Task<JobLease?> ClaimNextAsync(string workerId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var jobId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("""
            SELECT job_id FROM job_management.jobs
            WHERE reconciliation_required=false AND
                 ((state='Scheduled' AND COALESCE(next_attempt_at, scheduled_at)<=@now) OR
                  (state='Failed' AND retryable=true AND next_attempt_at<=@now))
            ORDER BY COALESCE(next_attempt_at, scheduled_at), job_id
            FOR UPDATE SKIP LOCKED LIMIT 1;
            """, new { now }, transaction, cancellationToken: cancellationToken));
        if (jobId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var current = await LockedAsync(connection, transaction, jobId, cancellationToken);
        var lease = await ClaimLockedAsync(connection, transaction, current, workerId, now, leaseDuration,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    public Task<DurableJobRecord> CompleteClaimAsync(JobLease lease, string? outcomeReference,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionClaimAsync(lease, JobState.Completed, null, false, null, outcomeReference, now,
            JobEvent.Completed, cancellationToken);

    public Task<DurableJobRecord> FailClaimAsync(JobLease lease, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionClaimAsync(lease, JobState.Failed, failureCode, retryable, nextAttemptAt, null, now,
            JobEvent.Failed, cancellationToken);

    public Task<DurableJobRecord> StartAsync(string jobId, DateTimeOffset now,
        CancellationToken cancellationToken) => TransitionAsync(jobId, [JobState.Scheduled], JobState.Running,
        null, false, null, null, now, JobEvent.Started, cancellationToken);

    public Task<DurableJobRecord> CompleteAsync(string jobId, string? outcomeReference, DateTimeOffset now,
        CancellationToken cancellationToken) => TransitionAsync(jobId,
        [JobState.Running, JobState.CancellationRequested], JobState.Completed, null, false, null,
        outcomeReference, now, JobEvent.Completed, cancellationToken);

    public Task<DurableJobRecord> FailAsync(string jobId, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(jobId, [JobState.Scheduled, JobState.Running, JobState.CancellationRequested],
            JobState.Failed, failureCode, retryable, nextAttemptAt, null, now, JobEvent.Failed,
            cancellationToken);

    public async Task<DurableJobRecord> CancelAsync(string jobId, string customerId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockedAsync(connection, transaction, jobId, cancellationToken);
        var pendingRetry = current.State == JobState.Failed && current.Retryable &&
            !current.ReconciliationRequired && current.LeaseToken is null;
        if (current.CustomerId != customerId || current.State is JobState.Completed or JobState.Cancelled ||
            (current.State == JobState.Failed && !pendingRetry))
            throw new InvalidOperationException("Invalid cancellation state.");
        var state = current.State == JobState.Scheduled || pendingRetry
            ? JobState.Cancelled
            : JobState.CancellationRequested;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state=@State, failure_code=@reason,
                retryable=false, next_attempt_at=NULL,
                cancellation_requested_at=@now, terminal_at=CASE WHEN @State='Cancelled' THEN @now ELSE NULL END,
                updated_at=@now WHERE job_id=@jobId;
            """, new { State = state.ToString(), reason, now, jobId }, transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(jobId, cancellationToken))!;
    }

    public async Task<DurableJobRecord> ReplayAsync(string jobId, string customerId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state='Scheduled', replay_count=replay_count+1,
                next_attempt_at=@now, terminal_at=NULL, reconciliation_required=false, updated_at=@now
            WHERE job_id=@jobId AND customer_id=@customerId AND state='Failed' AND retryable=true;
            """, new { jobId, customerId, now }, cancellationToken: cancellationToken));
        if (changed != 1) throw new InvalidOperationException("Job is not replayable.");
        return (await GetAsync(jobId, cancellationToken))!;
    }

    public async Task<int> RecoverInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<InterruptedAttemptRow>(new CommandDefinition("""
            SELECT job_id AS JobId, attempt AS Attempt, state AS State
            FROM job_management.jobs
            WHERE state IN ('Running','CancellationRequested') AND lease_expires_at<=@now
            ORDER BY job_id FOR UPDATE;
            """, new { now }, transaction, cancellationToken: cancellationToken))).ToArray();
        foreach (var row in rows)
        {
            var cancellationUnknown = row.State == JobState.CancellationRequested.ToString();
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE job_management.execution_attempts SET ended_at=@now, outcome=@outcome,
                    failure_code=@failureCode, retryable=false
                WHERE job_id=@JobId AND attempt=@Attempt AND ended_at IS NULL;
                """, new
            {
                now,
                outcome = cancellationUnknown ? "CANCELLATION_OUTCOME_UNKNOWN" : "OUTCOME_UNKNOWN",
                failureCode = cancellationUnknown
                    ? "job.cancellation.outcome-unknown"
                    : "job.execution.outcome-unknown",
                row.JobId,
                row.Attempt,
            }, transaction, cancellationToken: cancellationToken));
        }
        var recovered = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET
                state=CASE WHEN state='Running' THEN 'Failed' ELSE state END,
                failure_code=CASE WHEN state='Running' THEN 'job.execution.outcome-unknown'
                                  ELSE 'job.cancellation.outcome-unknown' END,
                retryable=false, next_attempt_at=NULL,
                terminal_at=CASE WHEN state='Running' THEN @now ELSE terminal_at END,
                lease_owner=NULL, lease_token=NULL, lease_expires_at=NULL,
                reconciliation_required=true, updated_at=@now
            WHERE state IN ('Running','CancellationRequested') AND lease_expires_at<=@now;
            """, new { now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    public async Task<DurableJobRecord> ReconcileAsync(string jobId, string customerId,
        JobReconciliationOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockedAsync(connection, transaction, jobId, cancellationToken);
        if (!current.ReconciliationRequired || current.CustomerId != customerId)
            throw new InvalidOperationException("The job is not eligible for reconciliation.");
        var state = outcome switch
        {
            JobReconciliationOutcome.ConfirmedNoEffect => JobState.Scheduled,
            JobReconciliationOutcome.ConfirmedCompleted => JobState.Completed,
            JobReconciliationOutcome.ConfirmedCancelled => JobState.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state=@state, failure_code=NULL, retryable=false,
                next_attempt_at=CASE WHEN @state='Scheduled' THEN @now ELSE NULL END,
                terminal_at=CASE WHEN @state IN ('Completed','Cancelled') THEN @now ELSE NULL END,
                reconciliation_required=false, updated_at=@now WHERE job_id=@jobId;
            """, new { state = state.ToString(), now, jobId }, transaction,
            cancellationToken: cancellationToken));
        var updated = await FindAsync(connection, transaction, jobId, cancellationToken)
            ?? throw new InvalidOperationException("Reconciled job is unavailable.");
        if (outcome == JobReconciliationOutcome.ConfirmedCompleted)
        {
            var message = JobEvents.Create(updated, JobEvent.Completed, now);
            await InsertOutboxAsync(connection, transaction, message, updated, JobEvent.Completed, now,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<JobExecutionAttemptRecord>> GetAttemptsAsync(string jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AttemptRow>(new CommandDefinition("""
            SELECT job_id AS JobId, attempt AS Attempt, started_at AS StartedAt,
                   ended_at AS EndedAt, outcome AS Outcome, failure_code AS FailureCode,
                   retryable AS Retryable
            FROM job_management.execution_attempts WHERE job_id=@jobId ORDER BY attempt;
            """, new { jobId }, cancellationToken: cancellationToken));
        return rows.Select(row => row.ToRecord()).ToArray();
    }

    public async Task<IReadOnlyList<AuditableEvent>> PendingEventsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OutboxRow>(new CommandDefinition("""
            SELECT event_id AS EventId, contract_id AS ContractId, event_name AS EventName,
                   event_version AS EventVersion, occurred_at AS OccurredAt,
                   correlation_id AS CorrelationId, causation_id AS CausationId, producer AS Producer,
                   subject_type AS SubjectType, subject_id AS SubjectId
            FROM job_management.outbox WHERE dispatched_at IS NULL ORDER BY created_at, event_id;
            """, cancellationToken: cancellationToken));
        return rows.Select(ToEvent).ToArray();
    }

    public async Task MarkDispatchedAsync(string eventId, DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job_management.outbox SET dispatched_at=@dispatchedAt WHERE event_id=@eventId AND dispatched_at IS NULL;",
            new { eventId, dispatchedAt }, cancellationToken: cancellationToken));
    }

    public async Task<JobOperationalSnapshot> GetOperationalSnapshotAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<MetricsRow>(new CommandDefinition("""
            SELECT count(*) FILTER (WHERE state='Scheduled')::int AS Queued,
                   count(*) FILTER (WHERE state IN ('Running','CancellationRequested'))::int AS Running,
                   count(*) FILTER (WHERE state='Completed')::int AS Completed,
                   count(*) FILTER (WHERE state='Failed')::int AS Failed,
                   count(*) FILTER (WHERE state='Cancelled')::int AS Cancelled,
                   coalesce(sum(retry_count),0)::int AS RetryCount,
                   coalesce(sum(replay_count),0)::int AS ReplayCount,
                   min(scheduled_at) FILTER (WHERE state='Scheduled') AS OldestRunnable,
                   (SELECT count(*)::int FROM job_management.outbox WHERE dispatched_at IS NULL) AS OutboxBacklog,
                   (SELECT min(created_at) FROM job_management.outbox WHERE dispatched_at IS NULL) AS OldestOutbox
            FROM job_management.jobs;
            """, cancellationToken: cancellationToken));
        return new(row.Queued, row.Running, row.Completed, row.Failed, row.Cancelled, row.RetryCount,
            row.ReplayCount,
            row.OldestRunnable is null ? null : now - Utc(row.OldestRunnable.Value), row.OutboxBacklog,
            row.OldestOutbox is null ? null : now - Utc(row.OldestOutbox.Value), 0, 0, 0, 0);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static bool IsEligible(DurableJobRecord job, DateTimeOffset now) =>
        !job.ReconciliationRequired &&
        ((job.State == JobState.Scheduled && (job.NextAttemptAt is null || job.NextAttemptAt <= now)) ||
         (job.State == JobState.Failed && job.Retryable && job.NextAttemptAt is not null && job.NextAttemptAt <= now));

    private static async Task<JobLease> ClaimLockedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, DurableJobRecord current, string workerId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now.Add(leaseDuration);
        var retryIncrement = current.State == JobState.Failed ? 1 : 0;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state='Running', attempt=attempt+1,
                retry_count=retry_count+@retryIncrement, failure_code=NULL, retryable=false,
                next_attempt_at=NULL, terminal_at=NULL, lease_owner=@workerId,
                lease_token=@leaseToken, lease_expires_at=@leaseExpiresAt,
                reconciliation_required=false, updated_at=@now WHERE job_id=@jobId;
            """, new { retryIncrement, workerId, leaseToken, leaseExpiresAt, now, current.JobId }, transaction,
            cancellationToken: cancellationToken));
        var updated = await FindAsync(connection, transaction, current.JobId, cancellationToken)
            ?? throw new InvalidOperationException("Claimed job is unavailable.");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_management.execution_attempts(job_id, attempt, started_at)
            VALUES (@JobId, @Attempt, @now);
            """, new { updated.JobId, updated.Attempt, now }, transaction,
            cancellationToken: cancellationToken));
        var message = JobEvents.Create(updated, JobEvent.Started, now);
        await InsertOutboxAsync(connection, transaction, message, updated, JobEvent.Started, now,
            cancellationToken);
        return new(updated, workerId, leaseToken, leaseExpiresAt);
    }

    private async Task<DurableJobRecord> TransitionClaimAsync(JobLease lease, JobState target,
        string? failureCode, bool retryable, DateTimeOffset? nextAttemptAt, string? outcomeReference,
        DateTimeOffset now, JobEvent kind, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockedAsync(connection, transaction, lease.Job.JobId, cancellationToken);
        if (current.State is not (JobState.Running or JobState.CancellationRequested) ||
            current.LeaseOwner != lease.WorkerId || current.LeaseToken != lease.LeaseToken)
            throw new InvalidOperationException("The durable job lease is no longer owned by this worker.");
        var cancellationUnknown = current.State == JobState.CancellationRequested && target == JobState.Failed;
        var finalState = cancellationUnknown ? JobState.CancellationRequested : target;
        var finalFailureCode = cancellationUnknown ? "job.cancellation.outcome-unknown" : failureCode;
        var finalRetryable = retryable && !cancellationUnknown;
        var finalNextAttemptAt = cancellationUnknown ? null : nextAttemptAt;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state=@state, failure_code=@failureCode,
                retryable=@retryable, next_attempt_at=@nextAttemptAt,
                outcome_reference=@outcomeReference,
                terminal_at=CASE WHEN @reconciliationRequired THEN NULL ELSE @now END,
                lease_owner=NULL, lease_token=NULL, lease_expires_at=NULL,
                reconciliation_required=@reconciliationRequired, updated_at=@now
            WHERE job_id=@jobId;
            """, new
        {
            state = finalState.ToString(),
            failureCode = finalFailureCode,
            retryable = finalRetryable,
            nextAttemptAt = finalNextAttemptAt,
            outcomeReference,
            reconciliationRequired = cancellationUnknown,
            now,
            jobId = current.JobId
        }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.execution_attempts SET ended_at=@now, outcome=@outcome,
                failure_code=@failureCode, retryable=@retryable
            WHERE job_id=@jobId AND attempt=@attempt;
            """, new
        {
            now,
            outcome = cancellationUnknown ? "CANCELLATION_OUTCOME_UNKNOWN" : finalState.ToString(),
            failureCode = finalFailureCode,
            retryable = finalRetryable,
            jobId = current.JobId,
            attempt = current.Attempt
        }, transaction, cancellationToken: cancellationToken));
        var updated = await FindAsync(connection, transaction, current.JobId, cancellationToken)
            ?? throw new InvalidOperationException("Updated job is unavailable.");
        var message = JobEvents.Create(updated, kind, now);
        await InsertOutboxAsync(connection, transaction, message, updated, kind, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task<DurableJobRecord> TransitionAsync(string jobId, JobState[] expected, JobState target,
        string? failureCode, bool retryable, DateTimeOffset? nextAttemptAt, string? outcomeReference,
        DateTimeOffset now, JobEvent kind, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockedAsync(connection, transaction, jobId, cancellationToken);
        if (!expected.Contains(current.State)) throw new InvalidOperationException("Invalid job state transition.");
        var cancellationUnknown = current.State == JobState.CancellationRequested && target == JobState.Failed;
        var finalState = cancellationUnknown ? JobState.CancellationRequested : target;
        var attempt = target == JobState.Running ? current.Attempt + 1 : current.Attempt;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE job_management.jobs SET state=@State, attempt=@attempt, failure_code=@failureCode,
                retryable=@retryable, next_attempt_at=@nextAttemptAt, outcome_reference=@outcomeReference,
                terminal_at=CASE WHEN @Terminal THEN @now ELSE NULL END,
                lease_owner=NULL, lease_token=NULL, lease_expires_at=NULL,
                reconciliation_required=@reconciliationRequired, updated_at=@now
            WHERE job_id=@jobId;
            """, new
        {
            State = finalState.ToString(),
            attempt,
            failureCode = cancellationUnknown ? "job.cancellation.outcome-unknown" : failureCode,
            retryable = retryable && !cancellationUnknown,
            nextAttemptAt = cancellationUnknown ? null : nextAttemptAt,
            outcomeReference,
            Terminal = finalState is JobState.Completed or JobState.Failed or JobState.Cancelled,
            reconciliationRequired = cancellationUnknown,
            now,
            jobId
        }, transaction, cancellationToken: cancellationToken));
        if (target == JobState.Running)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO job_management.execution_attempts(job_id, attempt, started_at)
                VALUES (@jobId, @attempt, @now);
                """, new { jobId, attempt, now }, transaction, cancellationToken: cancellationToken));
        }
        else if (current.Attempt > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE job_management.execution_attempts SET ended_at=@now, outcome=@outcome,
                    failure_code=@failureCode, retryable=@retryable
                WHERE job_id=@jobId AND attempt=@attempt;
                """, new
            {
                jobId,
                attempt = current.Attempt,
                now,
                outcome = cancellationUnknown ? "CANCELLATION_OUTCOME_UNKNOWN" : finalState.ToString(),
                failureCode = cancellationUnknown ? "job.cancellation.outcome-unknown" : failureCode,
                retryable = retryable && !cancellationUnknown
            }, transaction, cancellationToken: cancellationToken));
        }
        var updated = await FindAsync(connection, transaction, jobId, cancellationToken)
            ?? throw new InvalidOperationException("Updated job is unavailable.");
        var message = JobEvents.Create(updated, kind, now);
        await InsertOutboxAsync(connection, transaction, message, updated, kind, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AuditableEvent message, DurableJobRecord job, JobEvent kind, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        object payload = kind switch
        {
            JobEvent.Started => new JobStartedPayload(job.JobId, job.JobName, job.OwnerService,
                job.CustomerId, job.Attempt, now),
            JobEvent.Completed => new JobCompletedPayload(job.JobId, job.JobName, job.OwnerService,
                job.CustomerId, job.Attempt, job.OutcomeReference, now),
            JobEvent.Failed => new JobFailedPayload(job.JobId, job.JobName, job.OwnerService,
                job.CustomerId, job.Attempt, job.FailureCode ?? "job.execution.failed", job.Retryable, now),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO job_management.outbox
                (event_id, contract_id, event_name, event_version, occurred_at, correlation_id,
                 causation_id, producer, subject_type, subject_id, payload, created_at)
            VALUES (@EventId, @ContractId, @EventName, @EventVersion, @OccurredAt, @CorrelationId,
                    @CausationId, @Producer, @SubjectType, @SubjectId, CAST(@Payload AS jsonb), @CreatedAt)
            ON CONFLICT (event_id) DO NOTHING;
            """, new
        {
            message.EventId,
            message.ContractId,
            message.EventName,
            message.EventVersion,
            message.OccurredAt,
            message.CorrelationId,
            message.CausationId,
            message.Producer,
            message.SubjectType,
            message.SubjectId,
            Payload = JsonSerializer.Serialize(payload, ContractJson.Options),
            CreatedAt = now
        }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<DurableJobRecord> LockedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string jobId, CancellationToken cancellationToken) =>
        await FindAsync(connection, transaction, jobId, cancellationToken, true)
        ?? throw new InvalidOperationException("Job does not exist.");

    private static async Task<DurableJobRecord?> FindAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string jobId, CancellationToken cancellationToken, bool forUpdate = false)
    {
        var suffix = forUpdate ? " FOR UPDATE" : string.Empty;
        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition($"""
            SELECT job_id AS JobId, job_name AS JobName, job_version AS JobVersion,
                   owner_service AS OwnerService, payload_reference AS PayloadReference,
                   customer_id AS CustomerId, request_id AS RequestId, correlation_id AS CorrelationId,
                   causation_id AS CausationId, security_context::text AS SecurityJson,
                   idempotency_key AS IdempotencyKey, payload_fingerprint AS PayloadFingerprint,
                   state AS State, attempt AS Attempt, retry_count AS RetryCount,
                   replay_count AS ReplayCount,
                   requested_at AS RequestedAt, scheduled_at AS ScheduledAt,
                   next_attempt_at AS NextAttemptAt, cancellation_requested_at AS CancellationRequestedAt,
                   terminal_at AS TerminalAt, failure_code AS FailureCode, retryable AS Retryable,
                   outcome_reference AS OutcomeReference, lease_owner AS LeaseOwner,
                   lease_token AS LeaseToken, lease_expires_at AS LeaseExpiresAt,
                   reconciliation_required AS ReconciliationRequired, updated_at AS UpdatedAt
            FROM job_management.jobs WHERE job_id=@jobId{suffix};
            """, new { jobId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : row.ToRecord();
    }

    private static AuditableEvent ToEvent(OutboxRow row) => new(row.ContractId, row.EventId, row.EventName,
        row.EventVersion, Utc(row.OccurredAt), row.CorrelationId, row.CausationId, row.Producer,
        row.SubjectType, row.SubjectId);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class IdempotencyRow { public string JobId { get; set; } = string.Empty; public string PayloadFingerprint { get; set; } = string.Empty; }
    private sealed class JobRow
    {
        public string JobId { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        public string JobVersion { get; set; } = string.Empty;
        public string OwnerService { get; set; } = string.Empty;
        public string PayloadReference { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string? CausationId { get; set; }
        public string SecurityJson { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string PayloadFingerprint { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int Attempt { get; set; }
        public int RetryCount { get; set; }
        public int ReplayCount { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime ScheduledAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public DateTime? CancellationRequestedAt { get; set; }
        public DateTime? TerminalAt { get; set; }
        public string? FailureCode { get; set; }
        public bool Retryable { get; set; }
        public string? OutcomeReference { get; set; }
        public string? LeaseOwner { get; set; }
        public string? LeaseToken { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public bool ReconciliationRequired { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DurableJobRecord ToRecord() => new(JobId, JobName, JobVersion, OwnerService, PayloadReference,
            CustomerId, RequestId, CorrelationId, CausationId,
            JsonSerializer.Deserialize<TrustedSecurityContext>(SecurityJson, ContractJson.Options)!,
            IdempotencyKey, PayloadFingerprint, Enum.Parse<JobState>(State), Attempt, RetryCount, ReplayCount,
            Utc(RequestedAt), Utc(ScheduledAt), NextAttemptAt is null ? null : Utc(NextAttemptAt.Value),
            CancellationRequestedAt is null ? null : Utc(CancellationRequestedAt.Value),
            TerminalAt is null ? null : Utc(TerminalAt.Value), FailureCode, Retryable, OutcomeReference,
            LeaseOwner, LeaseToken, LeaseExpiresAt is null ? null : Utc(LeaseExpiresAt.Value), ReconciliationRequired,
            Utc(UpdatedAt));
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
    }
    private sealed class InterruptedAttemptRow
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
        public string State { get; set; } = string.Empty;
    }
    private sealed class AttemptRow
    {
        public string JobId { get; set; } = string.Empty;
        public int Attempt { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? Outcome { get; set; }
        public string? FailureCode { get; set; }
        public bool Retryable { get; set; }
        public JobExecutionAttemptRecord ToRecord() => new(JobId, Attempt, Utc(StartedAt),
            EndedAt is null ? null : Utc(EndedAt.Value), Outcome, FailureCode, Retryable);
    }
    private sealed class MetricsRow
    {
        public int Queued { get; set; }
        public int Running { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int Cancelled { get; set; }
        public int RetryCount { get; set; }
        public int ReplayCount { get; set; }
        public DateTime? OldestRunnable { get; set; }
        public int OutboxBacklog { get; set; }
        public DateTime? OldestOutbox { get; set; }
    }
}
