using Monergy.Contracts;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, ScheduledJob> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> idempotency = new(StringComparer.Ordinal);

    public Task<(ScheduledJob Job, bool Created)> ScheduleAsync(
        string idempotencyKey,
        ScheduledJob job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (idempotency.TryGetValue(idempotencyKey, out var existingId))
            {
                var existing = jobs[existingId];
                if (!string.Equals(existing.PayloadReference, job.PayloadReference, StringComparison.Ordinal) ||
                    !string.Equals(existing.CustomerId, job.CustomerId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Idempotency key conflict.");
                }

                return Task.FromResult((existing, false));
            }

            if (jobs.ContainsKey(job.JobId))
            {
                throw new InvalidOperationException("Job identity exists.");
            }

            jobs.Add(job.JobId, job);
            idempotency.Add(idempotencyKey, job.JobId);
            return Task.FromResult((job, true));
        }
    }

    public Task<ScheduledJob?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            jobs.TryGetValue(jobId, out var job);
            return Task.FromResult(job);
        }
    }

    public Task<ScheduledJob> TransitionAsync(
        string jobId,
        JobState expected,
        JobState target,
        string? failureCode,
        bool retryable,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!jobs.TryGetValue(jobId, out var current) || current.State != expected || !Allowed(expected, target))
            {
                throw new InvalidOperationException("Invalid job state transition.");
            }

            var next = current with
            {
                State = target,
                Attempt = target == JobState.Running ? current.Attempt + 1 : current.Attempt,
                FailureCode = failureCode,
                Retryable = retryable,
                UpdatedAt = updatedAt,
            };
            jobs[jobId] = next;
            return Task.FromResult(next);
        }
    }

    private static bool Allowed(JobState current, JobState target) =>
        (current, target) is
            (JobState.Scheduled, JobState.Running) or
            (JobState.Running, JobState.Completed) or
            (JobState.Running, JobState.Failed);
}
