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
                submission.PayloadFingerprint, JobState.Scheduled, 0, 0, submission.RequestedAt,
                submission.ScheduledAt, submission.ScheduledAt, null, null, null, false, null,
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

    public Task<DurableJobRecord> StartAsync(string jobId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Change(jobId, [JobState.Scheduled], current => current with
        {
            State = JobState.Running,
            Attempt = current.Attempt + 1,
            NextAttemptAt = null,
            FailureCode = null,
            Retryable = false,
            UpdatedAt = now,
        }, JobEvent.Started, now, cancellationToken);

    public Task<DurableJobRecord> CompleteAsync(string jobId, string? outcomeReference, DateTimeOffset now,
        CancellationToken cancellationToken) => Change(jobId, [JobState.Running, JobState.CancellationRequested],
        current => current.State == JobState.CancellationRequested
            ? current with { State = JobState.Cancelled, TerminalAt = now, UpdatedAt = now }
            : current with
            {
                State = JobState.Completed,
                OutcomeReference = outcomeReference,
                TerminalAt = now,
                UpdatedAt = now,
            }, JobEvent.Completed, now, cancellationToken, suppressEventWhenCancelled: true);

    public Task<DurableJobRecord> FailAsync(string jobId, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        Change(jobId, [JobState.Scheduled, JobState.Running, JobState.CancellationRequested], current => current with
        {
            State = current.State == JobState.CancellationRequested ? JobState.Cancelled : JobState.Failed,
            FailureCode = failureCode,
            Retryable = retryable && current.State != JobState.CancellationRequested,
            NextAttemptAt = retryable && current.State != JobState.CancellationRequested ? nextAttemptAt : null,
            TerminalAt = now,
            UpdatedAt = now,
        }, JobEvent.Failed, now, cancellationToken, suppressEventWhenCancelled: true);

    public Task<DurableJobRecord> CancelAsync(string jobId, string customerId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = Required(jobId);
            if (!string.Equals(current.CustomerId, customerId, StringComparison.Ordinal) ||
                current.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
                throw new InvalidOperationException("Invalid cancellation state.");
            var next = current.State == JobState.Scheduled
                ? current with { State = JobState.Cancelled, FailureCode = reason, TerminalAt = now, UpdatedAt = now }
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
            var interrupted = jobs.Values.Where(job => job.State == JobState.Running).ToArray();
            foreach (var job in interrupted)
                jobs[job.JobId] = job with
                {
                    State = JobState.Scheduled,
                    FailureCode = "job.execution.interrupted",
                    Retryable = true,
                    NextAttemptAt = now,
                    UpdatedAt = now
                };
            return Task.FromResult(interrupted.Length);
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
                values.Sum(job => job.ReplayCount),
                oldestRunnable is null ? null : now - oldestRunnable.Value,
                outbox.Count,
                oldestOutbox is null ? null : now - oldestOutbox.Value,
                dispatchAttempts, 0, 0, 0));
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
