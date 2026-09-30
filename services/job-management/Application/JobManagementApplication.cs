using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.Services.JobManagement.Application;

public sealed record JobSubmission(
    string ContractName,
    string ContractVersion,
    string IdempotencyKey,
    string PayloadFingerprint,
    string RequestId,
    string CorrelationId,
    string? CausationId,
    TrustedSecurityContext Security,
    string JobId,
    string JobName,
    string OwnerService,
    string PayloadReference,
    string CustomerId,
    DateTimeOffset RequestedAt,
    DateTimeOffset ScheduledAt);

public sealed record DurableJobRecord(
    string JobId,
    string JobName,
    string JobVersion,
    string OwnerService,
    string PayloadReference,
    string CustomerId,
    string RequestId,
    string CorrelationId,
    string? CausationId,
    TrustedSecurityContext Security,
    string IdempotencyKey,
    string PayloadFingerprint,
    JobState State,
    int Attempt,
    int RetryCount,
    int ReplayCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? CancellationRequestedAt,
    DateTimeOffset? TerminalAt,
    string? FailureCode,
    bool Retryable,
    string? OutcomeReference,
    string? LeaseOwner,
    string? LeaseToken,
    DateTimeOffset? LeaseExpiresAt,
    bool ReconciliationRequired,
    DateTimeOffset UpdatedAt)
{
    public ScheduledJob ToStatus() => new(JobId, JobName, OwnerService, PayloadReference, CustomerId,
        State, Attempt, FailureCode, Retryable, UpdatedAt);
}

public sealed record JobLease(
    DurableJobRecord Job,
    string WorkerId,
    string LeaseToken,
    DateTimeOffset ExpiresAt);

public enum JobReconciliationOutcome
{
    ConfirmedNoEffect,
    ConfirmedCompleted,
    ConfirmedCancelled,
}

public sealed record JobOperationalSnapshot(
    int Queued,
    int Running,
    int Completed,
    int Failed,
    int Cancelled,
    int RetryCount,
    int ReplayCount,
    TimeSpan? OldestRunnableAge,
    int OutboxBacklog,
    TimeSpan? OldestOutboxAge,
    int DispatchAttempts,
    int DispatchFailures,
    int DuplicateDeliveries,
    int AuditIngestionFailures);

public interface IJobRepository
{
    string AdapterKind { get; }
    Task<(DurableJobRecord Job, bool Created)> ScheduleAsync(JobSubmission submission, CancellationToken cancellationToken);
    Task<DurableJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task<JobLease?> ClaimAsync(string jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<JobLease?> ClaimNextAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    Task<DurableJobRecord> CompleteClaimAsync(JobLease lease, string? outcomeReference,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> FailClaimAsync(JobLease lease, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> StartAsync(string jobId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> CompleteAsync(string jobId, string? outcomeReference, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> FailAsync(string jobId, string failureCode, bool retryable,
        DateTimeOffset? nextAttemptAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> CancelAsync(string jobId, string customerId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> ReplayAsync(string jobId, string customerId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> RecoverInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<DurableJobRecord> ReconcileAsync(string jobId, string customerId, JobReconciliationOutcome outcome,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditableEvent>> PendingEventsAsync(CancellationToken cancellationToken);
    Task MarkDispatchedAsync(string eventId, DateTimeOffset dispatchedAt, CancellationToken cancellationToken);
    Task<JobOperationalSnapshot> GetOperationalSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IJobAuthorizationPolicy
{
    Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, DurableJobRecord job,
        string correlationId, CancellationToken cancellationToken);
}

public interface IJobConsentDecisionPort
{
    Task<ContractError?> EvaluateAsync(TrustedSecurityContext context, DurableJobRecord job,
        string correlationId, CancellationToken cancellationToken);
}

public interface IJobExecutionTarget
{
    Task<JobExecutionResult> ExecuteAsync(DurableJobRecord job, CancellationToken cancellationToken);
}

public sealed record JobExecutionResult(bool Succeeded, string? OutcomeReference, string? FailureCode, bool Retryable)
{
    public static JobExecutionResult Completed(string? outcomeReference) => new(true, outcomeReference, null, false);
    public static JobExecutionResult Failed(string failureCode, bool retryable) => new(false, null, failureCode, retryable);
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
        if (contextError is not null) return Reject<ScheduledJob, ScheduleJob>(request, contextError);

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
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(payload, ContractJson.Options)))).ToLowerInvariant();
            var scheduled = await repository.ScheduleAsync(new(
                request.ContractName, request.ContractVersion, request.IdempotencyKey, fingerprint,
                request.RequestId, request.CorrelationId, request.CausationId, request.Security,
                payload.JobId, payload.JobName, payload.OwnerService, payload.PayloadReference,
                payload.CustomerId, payload.RequestedAt, now), cancellationToken);
            Record(request.ContractName, scheduled.Created ? "SCHEDULED" : "REPLAYED", request.RequestId,
                request.CorrelationId, request.CausationId, scheduled.Job.JobId);
            return ContractResult<ScheduledJob>.Succeeded(request, scheduled.Job.ToStatus());
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ScheduledJob>.Rejected(
                request, "job.schedule.conflict", ContractErrorCategory.Conflict,
                "The job identity or idempotency key conflicts.");
        }
    }

    public async Task<ContractResult<ScheduledJob>> GetJobStatusAsync(
        ContractRequest<GetJobStatus> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetJobStatus);
        if (contextError is not null) return Reject<ScheduledJob, GetJobStatus>(request, contextError);

        var job = await repository.GetAsync(request.Payload.JobId, cancellationToken);
        if (job is null ||
            !string.Equals(job.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(job.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ScheduledJob>.Rejected(
                request, "job.not-found", ContractErrorCategory.NotFound, "Authorized job status was not found.");
        }

        return ContractResult<ScheduledJob>.Succeeded(request, job.ToStatus());
    }

    public async Task<ContractResult<ScheduledJob>> CancelJobAsync(
        ContractRequest<CancelJob> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.CancelJob);
        if (contextError is not null) return Reject<ScheduledJob, CancelJob>(request, contextError);
        if (string.IsNullOrWhiteSpace(request.Payload.JobId) || string.IsNullOrWhiteSpace(request.Payload.Reason) ||
            !string.Equals(request.Payload.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ScheduledJob>.Rejected(request, "job.cancel.invalid",
                ContractErrorCategory.ValidationError, "The cancellation request is invalid.");
        }

        try
        {
            var job = await repository.CancelAsync(request.Payload.JobId, request.Payload.CustomerId,
                request.Payload.Reason, timeProvider.GetUtcNow(), cancellationToken);
            Record(request.ContractName, job.State == JobState.Cancelled ? "CANCELLED" : "CANCELLATION_REQUESTED",
                request.RequestId, request.CorrelationId, request.CausationId, job.JobId);
            return ContractResult<ScheduledJob>.Succeeded(request, job.ToStatus());
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ScheduledJob>.Rejected(request, "job.cancel.conflict",
                ContractErrorCategory.Conflict, "The current job state cannot be cancelled.");
        }
    }

    public async Task<ContractResult<ScheduledJob>> ReplayAsync(
        ContractRequest<GetJobStatus> request,
        CancellationToken cancellationToken = default)
    {
        var contextError = ContractGuard.Validate(request, Vs02ContractNames.GetJobStatus);
        if (contextError is not null) return Reject<ScheduledJob, GetJobStatus>(request, contextError);
        var persisted = await repository.GetAsync(request.Payload.JobId, cancellationToken);
        if (persisted is null ||
            !string.Equals(persisted.CustomerId, request.Payload.CustomerId, StringComparison.Ordinal) ||
            !string.Equals(persisted.CustomerId, request.Security.Access.CustomerId, StringComparison.Ordinal))
        {
            return ContractResult<ScheduledJob>.Rejected(request, "job.replay.denied",
                ContractErrorCategory.AccessDenied, "The job is not available to the trusted customer context.");
        }
        try
        {
            var job = await repository.ReplayAsync(request.Payload.JobId, request.Payload.CustomerId,
                timeProvider.GetUtcNow(), cancellationToken);
            return ContractResult<ScheduledJob>.Succeeded(request, job.ToStatus());
        }
        catch (InvalidOperationException)
        {
            return ContractResult<ScheduledJob>.Rejected(request, "job.replay.conflict",
                ContractErrorCategory.Conflict, "The job is not eligible for safe replay.");
        }
    }

    public async Task<ContractResult<ScheduledJob>> ExecuteAsync(string jobId,
        IJobAuthorizationPolicy authorization, IJobConsentDecisionPort consent,
        IJobExecutionTarget target, CancellationToken cancellationToken = default)
    {
        var lease = await repository.ClaimAsync(jobId, $"direct-{Guid.NewGuid():N}",
            timeProvider.GetUtcNow(), TimeSpan.FromMinutes(2), cancellationToken)
            ?? throw new InvalidOperationException("The job is not eligible for execution.");
        return await ExecuteClaimedAsync(lease, authorization, consent, target, cancellationToken);
    }

    public async Task<ContractResult<ScheduledJob>> ExecuteClaimedAsync(JobLease lease,
        IJobAuthorizationPolicy authorization, IJobConsentDecisionPort consent,
        IJobExecutionTarget target, CancellationToken cancellationToken = default)
    {
        var job = lease.Job;
        var executionRequest = new ContractRequest<object?>(job.JobName, job.JobVersion, job.RequestId,
            job.CorrelationId, job.CausationId, job.Security, job.IdempotencyKey, null);
        var securityError = !string.Equals(job.CustomerId, job.Security.Access.CustomerId, StringComparison.Ordinal)
            ? new ContractError("job.security.customer-mismatch", ContractErrorCategory.AccessDenied,
                "The persisted job and trusted customer context do not match.", false, job.CorrelationId)
            : ContractGuard.Validate(executionRequest, job.JobName);
        var authorizationError = securityError ?? await authorization.AuthorizeAsync(
            job.Security, job, job.CorrelationId, cancellationToken);
        var consentError = authorizationError is null && job.Security.Access.ConsentReferenceId is not null
            ? await consent.EvaluateAsync(job.Security, job, job.CorrelationId, cancellationToken)
            : null;
        var decisionError = authorizationError ?? consentError;
        if (decisionError is not null)
        {
            var denied = await repository.FailClaimAsync(lease, decisionError.Code, false, null,
                timeProvider.GetUtcNow(), cancellationToken);
            return FailedResult(denied, decisionError);
        }

        var outcome = await target.ExecuteAsync(job, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var terminal = outcome.Succeeded
            ? await repository.CompleteClaimAsync(lease, outcome.OutcomeReference, now, cancellationToken)
            : await repository.FailClaimAsync(lease, outcome.FailureCode ?? "job.execution.failed", outcome.Retryable,
                outcome.Retryable ? now.AddMinutes(1) : null, now, cancellationToken);
        return outcome.Succeeded
            ? SuccessResult(terminal)
            : FailedResult(terminal, new(outcome.FailureCode ?? "job.execution.failed",
                ContractErrorCategory.ProcessingFailed, "The delegated domain execution failed.", outcome.Retryable,
                terminal.CorrelationId));
    }

    public Task<ScheduledJob> StartAsync(string jobId, CancellationToken cancellationToken = default) =>
        TransitionStatus(repository.StartAsync(jobId, timeProvider.GetUtcNow(), cancellationToken));

    public Task<ScheduledJob> CompleteAsync(string jobId, CancellationToken cancellationToken = default) =>
        TransitionStatus(repository.CompleteAsync(jobId, null, timeProvider.GetUtcNow(), cancellationToken));

    public Task<ScheduledJob> FailAsync(string jobId, string failureCode, bool retryable,
        CancellationToken cancellationToken = default) =>
        TransitionStatus(repository.FailAsync(jobId, failureCode, retryable,
            retryable ? timeProvider.GetUtcNow() : null, timeProvider.GetUtcNow(), cancellationToken));

    public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) =>
        repository.RecoverInterruptedAsync(timeProvider.GetUtcNow(), cancellationToken);

    public Task<DurableJobRecord> ReconcileAsync(string jobId, string customerId,
        JobReconciliationOutcome outcome, CancellationToken cancellationToken = default) =>
        repository.ReconcileAsync(jobId, customerId, outcome, timeProvider.GetUtcNow(), cancellationToken);

    private void Record(string operation, string outcome, string requestId, string correlationId,
        string? causationId, string jobId) => telemetry.Record(new("Job Management Service", operation, outcome,
        requestId, correlationId, causationId, "processing-job", jobId, repository.AdapterKind));

    private static async Task<ScheduledJob> TransitionStatus(Task<DurableJobRecord> transition) =>
        (await transition).ToStatus();

    private static ContractResult<ScheduledJob> SuccessResult(DurableJobRecord job) => new(job.JobName,
        job.JobVersion, job.RequestId, job.CorrelationId, ContractOutcome.Success, job.ToStatus(), null);

    private static ContractResult<ScheduledJob> FailedResult(DurableJobRecord job, ContractError error) => new(
        job.JobName, job.JobVersion, job.RequestId, job.CorrelationId, ContractOutcome.Failed,
        job.ToStatus(), error);

    private static ContractResult<TData> Reject<TData, TPayload>(ContractRequest<TPayload> request,
        ContractError error) => new(request.ContractName, request.ContractVersion, request.RequestId,
        request.CorrelationId, ContractOutcome.Rejected, default, error);
}
