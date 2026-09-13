using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.JobManagement.Application;

public interface IJobRepository
{
    Task<(ScheduledJob Job, bool Created)> ScheduleAsync(
        string idempotencyKey,
        ScheduledJob job,
        CancellationToken cancellationToken);

    Task<ScheduledJob?> GetAsync(string jobId, CancellationToken cancellationToken);

    Task<ScheduledJob> TransitionAsync(
        string jobId,
        JobState expected,
        JobState target,
        string? failureCode,
        bool retryable,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}

public sealed class JobManagementApplication(
    IJobRepository repository,
    ILifecycleTelemetry telemetry,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedOwners =
        new(StringComparer.Ordinal)
        {
            "Document Intelligence Service",
            "Search & Retrieval Service",
            "Reporting Service",
            "Integration Gateway Service",
        };

    public async Task<ContractResult<ScheduledJob>> ScheduleJobAsync(
        ContractRequest<ScheduleJob> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.ScheduleJob);
        if (contextError is not null)
        {
            return Reject<ScheduledJob, ScheduleJob>(request, contextError);
        }

        var payload = request.Payload;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(payload.JobId) ||
            string.IsNullOrWhiteSpace(payload.JobName) ||
            string.IsNullOrWhiteSpace(payload.PayloadReference) ||
            !AllowedOwners.Contains(payload.OwnerService) ||
            !string.Equals(payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ScheduledJob>.Rejected(
                request, "job.schedule.invalid", ContractErrorCategory.ValidationError, "The durable job request is invalid.");
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var candidate = new ScheduledJob(
                payload.JobId,
                payload.JobName,
                payload.OwnerService,
                payload.PayloadReference,
                payload.CustomerId,
                JobState.Scheduled,
                0,
                null,
                false,
                now);
            var scheduled = await repository.ScheduleAsync(request.IdempotencyKey, candidate, cancellationToken);
            telemetry.Record(new LifecycleSignal(
                "Job Management Service",
                Vs02ContractNames.ScheduleJob,
                scheduled.Created ? "SCHEDULED" : "REPLAYED",
                request.RequestId,
                request.CorrelationId,
                request.CausationId,
                "processing-job",
                scheduled.Job.JobId,
                "IN_MEMORY_REFERENCE"));
            return ContractResult<ScheduledJob>.Succeeded(request, scheduled.Job);
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ScheduledJob>.Rejected(
                request, "job.schedule.conflict", ContractErrorCategory.Conflict, "The job identity or idempotency key conflicts.");
        }
    }

    public async Task<ContractResult<ScheduledJob>> GetJobStatusAsync(
        ContractRequest<GetJobStatus> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetJobStatus);
        if (contextError is not null)
        {
            return Reject<ScheduledJob, GetJobStatus>(request, contextError);
        }

        var job = await repository.GetAsync(request.Payload.JobId, cancellationToken);
        if (job is null ||
            !string.Equals(job.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(job.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ScheduledJob>.Rejected(
                request, "job.not-found", ContractErrorCategory.NotFound, "Authorized job status was not found.");
        }

        return ContractResult<ScheduledJob>.Succeeded(request, job);
    }

    public Task<ScheduledJob> StartAsync(string jobId, CancellationToken cancellationToken = default) =>
        repository.TransitionAsync(jobId, JobState.Scheduled, JobState.Running, null, false, timeProvider.GetUtcNow(), cancellationToken);

    public Task<ScheduledJob> CompleteAsync(string jobId, CancellationToken cancellationToken = default) =>
        repository.TransitionAsync(jobId, JobState.Running, JobState.Completed, null, false, timeProvider.GetUtcNow(), cancellationToken);

    public Task<ScheduledJob> FailAsync(
        string jobId,
        string failureCode,
        bool retryable,
        CancellationToken cancellationToken = default) =>
        repository.TransitionAsync(jobId, JobState.Running, JobState.Failed, failureCode, retryable, timeProvider.GetUtcNow(), cancellationToken);

    private static ContractResult<TData> Reject<TData, TPayload>(
        ContractRequest<TPayload> request,
        ContractError error) =>
        new(request.ContractName, request.ContractVersion, request.RequestId, request.CorrelationId, ContractOutcome.Rejected, default, error);
}
