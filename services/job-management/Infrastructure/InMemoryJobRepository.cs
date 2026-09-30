using Monergy.Contracts;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed class InMemoryJobRepository : IJobRepository
{
    private sealed record OutboxEntry(AuditableEvent Event, DateTimeOffset CreatedAt);

    private readonly object sync = new();
    private readonly Dictionary<string, DurableJobRecord> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string JobId, string Fingerprint)> idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboxEntry> outbox = new(StringComparer.Ordinal);
    private readonly Dictionary<(string JobId, int Attempt), JobExecutionAttemptRecord> attempts = [];
    private int dispatchAttempts;

    public string AdapterKind => "IN_MEMORY_REFERENCE";

    public Task<(DurableJobRecord Job, bool Created)> ScheduleAsync(
        JobSubmission submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var identity = Identity(submission);
            if (idempotency.TryGetValue(identity, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, submission.PayloadFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Idempotency key conflict.");
                return Task.FromResult((jobs[prior.JobId], false));
            }
            if (jobs.ContainsKey(submission.JobId)) throw new InvalidOperationException("Job identity exists.");

            var job = new DurableJobRecord(submission.JobId, submission.JobName, submission.ContractVersion,
                submission.OwnerService, submission.PayloadReference, submission.CustomerId, submission.RequestId,
                submission.CorrelationId, submission.CausationId, submission.Security, submission.IdempotencyKey,
                submission.PayloadFingerprint, JobState.Scheduled, 0, 0, 0, submission.RequestedAt,
                submission.ScheduledAt, submission.ScheduledAt, null, null, null, false, null,
                null, null, null, false,
                submission.ScheduledAt);
            jobs.Add(job.JobId, job);
            idempotency.Add(identity, (job.JobId, submission.PayloadFingerprint));
            return Task.FromResult((job, true));
        }
    }

    public Task<DurableJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(jobs.GetValueOrDefault(jobId));
    }

    public Task<JobLease?> ClaimAsync(string jobId, string workerId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!jobs.TryGetValue(jobId, out var current)) return Task.FromResult<JobLease?>(null);
            return Task.FromResult(Claim(current, workerId, now, leaseDuration));
        }
    }

    public Task<JobLease?> ClaimNextAsync(string workerId, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var candidate = jobs.Values
                .Where(job => IsEligible(job, now))
                .OrderBy(job => job.NextAttemptAt ?? job.ScheduledAt)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .FirstOrDefault();
            return Task.FromResult(candidate is null ? null : Claim(candidate, workerId, now, leaseDuration));
        }
    }

    public Task<DurableJobRecord> CompleteClaimAsync(JobLease lease, string? outcomeReference,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeClaim(lease, current => current with
        {
            State = JobState.Completed,
            OutcomeReference = outcomeReference,
            FailureCode = null,
            TerminalAt = now,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
            Retryable = false,
            NextAttemptAt = null,
            ReconciliationRequired = false,
            UpdatedAt = now,
        }, JobEvent.Completed, now, cancellationToken);

    public Task<DurableJobRecord> FailClaimAsync(JobLease lease, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeClaim(lease, current => current.State == JobState.CancellationRequested
            ? current with
            {
                FailureCode = "job.cancellation.outcome-unknown",
                Retryable = false,
                NextAttemptAt = null,
                TerminalAt = null,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                ReconciliationRequired = true,
                UpdatedAt = now,
            }
            : current with
            {
                State = JobState.Failed,
                FailureCode = failureCode,
                Retryable = retryable,
                NextAttemptAt = retryable ? nextAttemptAt : null,
                TerminalAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                UpdatedAt = now,
            }, JobEvent.Failed, now, cancellationToken);

    public Task<DurableJobRecord> StartAsync(string jobId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Change(jobId, [JobState.Scheduled], current => current with
        {
            State = JobState.Running,
            Attempt = current.Attempt + 1,
            NextAttemptAt = null,
            FailureCode = null,
            Retryable = false,
            ReconciliationRequired = false,
            UpdatedAt = now,
        }, JobEvent.Started, now, cancellationToken);

    public Task<DurableJobRecord> CompleteAsync(string jobId, string? outcomeReference, DateTimeOffset now,
        CancellationToken cancellationToken) => Change(jobId, [JobState.Running, JobState.CancellationRequested],
        current => current.State == JobState.CancellationRequested
            ? current with
            {
                State = JobState.Completed,
                OutcomeReference = outcomeReference,
                FailureCode = null,
                Retryable = false,
                NextAttemptAt = null,
                TerminalAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                ReconciliationRequired = false,
                UpdatedAt = now,
            }
            : current with
            {
                State = JobState.Completed,
                OutcomeReference = outcomeReference,
                TerminalAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                UpdatedAt = now,
            }, JobEvent.Completed, now, cancellationToken);

    public Task<DurableJobRecord> FailAsync(string jobId, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        Change(jobId, [JobState.Scheduled, JobState.Running, JobState.CancellationRequested], current => current with
        {
            State = current.State == JobState.CancellationRequested ? JobState.CancellationRequested : JobState.Failed,
            FailureCode = current.State == JobState.CancellationRequested
                ? "job.cancellation.outcome-unknown"
                : failureCode,
            Retryable = retryable && current.State != JobState.CancellationRequested,
            NextAttemptAt = retryable && current.State != JobState.CancellationRequested ? nextAttemptAt : null,
            TerminalAt = current.State == JobState.CancellationRequested ? null : now,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
            ReconciliationRequired = current.State == JobState.CancellationRequested,
            UpdatedAt = now,
        }, JobEvent.Failed, now, cancellationToken);

    public Task<DurableJobRecord> CancelAsync(string jobId, string customerId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(jobId);
            var pendingRetry = current.State == JobState.Failed && current.Retryable &&
                !current.ReconciliationRequired && current.LeaseToken is null;
            if (!string.Equals(current.CustomerId, customerId, StringComparison.Ordinal) ||
                current.State is JobState.Completed or JobState.Cancelled ||
                (current.State == JobState.Failed && !pendingRetry))
                throw new InvalidOperationException("Invalid cancellation state.");
            var next = current.State == JobState.Scheduled || pendingRetry
                ? current with
                {
                    State = JobState.Cancelled,
                    FailureCode = reason,
                    Retryable = false,
                    NextAttemptAt = null,
                    CancellationRequestedAt = now,
                    TerminalAt = now,
                    UpdatedAt = now,
                }
                : current with
                {
                    State = JobState.CancellationRequested,
                    FailureCode = reason,
                    CancellationRequestedAt = now,
                    UpdatedAt = now
                };
            jobs[jobId] = next;
            return Task.FromResult(next);
        }
    }

    public Task<DurableJobRecord> ReplayAsync(string jobId, string customerId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(jobId);
            if (!string.Equals(current.CustomerId, customerId, StringComparison.Ordinal) ||
                current.State != JobState.Failed || !current.Retryable)
                throw new InvalidOperationException("Job is not replayable.");
            var next = current with
            {
                State = JobState.Scheduled,
                ReplayCount = current.ReplayCount + 1,
                NextAttemptAt = now,
                TerminalAt = null,
                ReconciliationRequired = false,
                UpdatedAt = now
            };
            jobs[jobId] = next;
            return Task.FromResult(next);
        }
    }

    public Task<int> RecoverInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var interrupted = jobs.Values.Where(job =>
                (job.State is JobState.Running or JobState.CancellationRequested) &&
                job.LeaseExpiresAt is not null && job.LeaseExpiresAt <= now).ToArray();
            foreach (var job in interrupted)
            {
                CloseAttempt(job, now, job.State == JobState.Running
                    ? "OUTCOME_UNKNOWN"
                    : "CANCELLATION_OUTCOME_UNKNOWN", job.State == JobState.Running
                    ? "job.execution.outcome-unknown"
                    : "job.cancellation.outcome-unknown", false);
                jobs[job.JobId] = job with
                {
                    State = job.State == JobState.Running ? JobState.Failed : JobState.CancellationRequested,
                    FailureCode = job.State == JobState.Running
                        ? "job.execution.outcome-unknown"
                        : "job.cancellation.outcome-unknown",
                    Retryable = false,
                    NextAttemptAt = null,
                    TerminalAt = job.State == JobState.Running ? now : null,
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                    ReconciliationRequired = true,
                    UpdatedAt = now
                };
            }
            return Task.FromResult(interrupted.Length);
        }
    }

    public Task<IReadOnlyList<JobExecutionAttemptRecord>> GetAttemptsAsync(string jobId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult<IReadOnlyList<JobExecutionAttemptRecord>>(attempts.Values
            .Where(attempt => attempt.JobId == jobId).OrderBy(attempt => attempt.Attempt).ToArray());
    }

    public Task<DurableJobRecord> ReconcileAsync(string jobId, string customerId,
        JobReconciliationOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(jobId);
            if (!current.ReconciliationRequired ||
                !string.Equals(current.CustomerId, customerId, StringComparison.Ordinal))
                throw new InvalidOperationException("The job is not eligible for reconciliation.");
            var next = outcome switch
            {
                JobReconciliationOutcome.ConfirmedNoEffect => current with
                {
                    State = JobState.Scheduled,
                    FailureCode = null,
                    Retryable = false,
                    NextAttemptAt = now,
                    TerminalAt = null,
                    ReconciliationRequired = false,
                    UpdatedAt = now,
                },
                JobReconciliationOutcome.ConfirmedCompleted => current with
                {
                    State = JobState.Completed,
                    FailureCode = null,
                    Retryable = false,
                    NextAttemptAt = null,
                    TerminalAt = now,
                    ReconciliationRequired = false,
                    UpdatedAt = now,
                },
                JobReconciliationOutcome.ConfirmedCancelled => current with
                {
                    State = JobState.Cancelled,
                    Retryable = false,
                    NextAttemptAt = null,
                    TerminalAt = now,
                    ReconciliationRequired = false,
                    UpdatedAt = now,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            jobs[jobId] = next;
            if (outcome == JobReconciliationOutcome.ConfirmedCompleted)
            {
                var message = JobEvents.Create(next, JobEvent.Completed, now);
                outbox.TryAdd(message.EventId, new(message, now));
            }
            return Task.FromResult(next);
        }
    }

    public Task<IReadOnlyList<AuditableEvent>> PendingEventsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult<IReadOnlyList<AuditableEvent>>(
            outbox.Values.OrderBy(entry => entry.CreatedAt).Select(entry => entry.Event).ToArray());
    }

    public Task MarkDispatchedAsync(string eventId, DateTimeOffset dispatchedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) { dispatchAttempts++; outbox.Remove(eventId); }
        return Task.CompletedTask;
    }

    public Task<JobOperationalSnapshot> GetOperationalSnapshotAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var values = jobs.Values.ToArray();
            var oldestRunnable = values.Where(job => job.State == JobState.Scheduled)
                .Select(job => (DateTimeOffset?)job.ScheduledAt).Min();
            var oldestOutbox = outbox.Values.Select(entry => (DateTimeOffset?)entry.CreatedAt).Min();
            return Task.FromResult(new JobOperationalSnapshot(
                values.Count(job => job.State == JobState.Scheduled),
                values.Count(job => job.State is JobState.Running or JobState.CancellationRequested),
                values.Count(job => job.State == JobState.Completed),
                values.Count(job => job.State == JobState.Failed),
                values.Count(job => job.State == JobState.Cancelled),
                values.Sum(job => job.RetryCount),
                values.Sum(job => job.ReplayCount),
                oldestRunnable is null ? null : now - oldestRunnable.Value,
                outbox.Count,
                oldestOutbox is null ? null : now - oldestOutbox.Value,
                dispatchAttempts, 0, 0, 0));
        }
    }

    private JobLease? Claim(DurableJobRecord current, string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (!IsEligible(current, now)) return null;
        var retry = current.State == JobState.Failed;
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = now.Add(leaseDuration);
        var next = current with
        {
            State = JobState.Running,
            Attempt = current.Attempt + 1,
            RetryCount = current.RetryCount + (retry ? 1 : 0),
            NextAttemptAt = null,
            FailureCode = null,
            Retryable = false,
            TerminalAt = null,
            LeaseOwner = workerId,
            LeaseToken = token,
            LeaseExpiresAt = expiresAt,
            ReconciliationRequired = false,
            UpdatedAt = now,
        };
        jobs[next.JobId] = next;
        attempts[(next.JobId, next.Attempt)] = new(next.JobId, next.Attempt, now, null, null, null, false);
        var message = JobEvents.Create(next, JobEvent.Started, now);
        outbox.TryAdd(message.EventId, new(message, now));
        return new(next, workerId, token, expiresAt);
    }

    private static bool IsEligible(DurableJobRecord job, DateTimeOffset now) =>
        !job.ReconciliationRequired &&
        ((job.State == JobState.Scheduled && (job.NextAttemptAt is null || job.NextAttemptAt <= now)) ||
         (job.State == JobState.Failed && job.Retryable && job.NextAttemptAt is not null && job.NextAttemptAt <= now));

    private Task<DurableJobRecord> ChangeClaim(JobLease lease,
        Func<DurableJobRecord, DurableJobRecord> transition, JobEvent jobEvent, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(lease.Job.JobId);
            if (current.State is not (JobState.Running or JobState.CancellationRequested) ||
                !string.Equals(current.LeaseOwner, lease.WorkerId, StringComparison.Ordinal) ||
                !string.Equals(current.LeaseToken, lease.LeaseToken, StringComparison.Ordinal))
                throw new InvalidOperationException("The durable job lease is no longer owned by this worker.");
            var next = transition(current);
            jobs[next.JobId] = next;
            var cancellationUnknown = current.State == JobState.CancellationRequested &&
                jobEvent == JobEvent.Failed;
            CloseAttempt(current, now, cancellationUnknown ? "CANCELLATION_OUTCOME_UNKNOWN" : next.State.ToString(),
                next.FailureCode, next.Retryable);
            var message = JobEvents.Create(next, jobEvent, now);
            outbox.TryAdd(message.EventId, new(message, now));
            return Task.FromResult(next);
        }
    }

    private Task<DurableJobRecord> Change(string jobId, JobState[] expected,
        Func<DurableJobRecord, DurableJobRecord> transition, JobEvent jobEvent, DateTimeOffset now,
        CancellationToken cancellationToken, bool suppressEventWhenCancelled = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(jobId);
            if (!expected.Contains(current.State)) throw new InvalidOperationException("Invalid job state transition.");
            var next = transition(current);
            jobs[jobId] = next;
            if (jobEvent == JobEvent.Started)
                attempts[(next.JobId, next.Attempt)] = new(next.JobId, next.Attempt, now, null, null, null, false);
            else if (current.Attempt > 0)
            {
                var cancellationUnknown = current.State == JobState.CancellationRequested &&
                    jobEvent == JobEvent.Failed;
                CloseAttempt(current, now,
                    cancellationUnknown ? "CANCELLATION_OUTCOME_UNKNOWN" : next.State.ToString(),
                    next.FailureCode, next.Retryable);
            }
            if (!(suppressEventWhenCancelled && next.State == JobState.Cancelled))
            {
                var message = JobEvents.Create(next, jobEvent, now);
                outbox.TryAdd(message.EventId, new(message, now));
            }
            return Task.FromResult(next);
        }
    }

    private DurableJobRecord Required(string jobId) => jobs.TryGetValue(jobId, out var job)
        ? job : throw new InvalidOperationException("Job does not exist.");

    private void CloseAttempt(DurableJobRecord job, DateTimeOffset endedAt, string outcome,
        string? failureCode, bool retryable)
    {
        if (!attempts.TryGetValue((job.JobId, job.Attempt), out var attempt)) return;
        attempts[(job.JobId, job.Attempt)] = attempt with
        {
            EndedAt = endedAt,
            Outcome = outcome,
            FailureCode = failureCode,
            Retryable = retryable,
        };
    }

    private static string Identity(JobSubmission value) => string.Join('|', value.ContractName,
        value.ContractVersion, value.CustomerId, value.IdempotencyKey);
}

internal enum JobEvent { Started, Completed, Failed }

internal static class JobEvents
{
    public static AuditableEvent Create(DurableJobRecord job, JobEvent kind, DateTimeOffset occurredAt)
    {
        var (contractId, eventName) = kind switch
        {
            JobEvent.Started => ("CID-058", Vs02ContractNames.JobStarted),
            JobEvent.Completed => ("CID-059", Vs02ContractNames.JobCompleted),
            JobEvent.Failed => ("CID-060", Vs02ContractNames.JobFailed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new(contractId, $"{job.JobId}:{contractId}:{job.Attempt}:{job.ReplayCount}", eventName,
            ContractGuard.CurrentVersion, occurredAt, job.CorrelationId, job.CausationId,
            "Job Management Service", "processing-job", job.JobId);
    }
}
