using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Monergy.Contracts;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;
using Xunit;

namespace Monergy.JobManagement.Tests;

public sealed class DurableJobBehaviorTests
{
    [Fact]
    public async Task DuplicateSubmissionReplaysAndConflictingPayloadIsRejected()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        var request = JobTestContext.Schedule("job-duplicate", "same-key");

        var first = await app.ScheduleJobAsync(request);
        var replay = await app.ScheduleJobAsync(request);
        var conflict = await app.ScheduleJobAsync(JobTestContext.Schedule("job-conflict", "same-key", "other"));

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(first.Data, replay.Data);
        Assert.Equal(ContractErrorCategory.Conflict, conflict.Error?.Category);
    }

    [Fact]
    public async Task AutomaticRetryReevaluatesAuthorizationAndConsentWithoutReplay()
    {
        var repository = new InMemoryJobRepository();
        var clock = new MutableTimeProvider(JobTestContext.Now);
        var app = App(repository, clock);
        var policy = Policy(clock);
        var target = new SequenceTarget(
            JobExecutionResult.Failed("dependency.timeout.unknown", true),
            JobExecutionResult.Completed("document-result"));
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-retry", "key-retry"));
        var registry = new ReferenceJobExecutionTargetRegistry();
        registry.Register("Document Intelligence Service", Vs02ContractNames.ProcessDocument, target);
        var worker = new DurableJobExecutionWorker(ReferenceConfiguration(), repository, app, policy, policy,
            registry, clock, NullLogger<DurableJobExecutionWorker>.Instance);

        Assert.True(await worker.RunOnceAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await worker.RunOnceAsync());

        Assert.Equal(2, target.Calls);
        Assert.Equal(2, policy.AuthorizationEvaluations);
        Assert.Equal(2, policy.ConsentEvaluations);
        var snapshot = await repository.GetOperationalSnapshotAsync(clock.Now, default);
        Assert.Equal(1, snapshot.RetryCount);
        Assert.Equal(0, snapshot.ReplayCount);
        Assert.Equal(JobState.Completed, (await repository.GetAsync("job-retry", default))?.State);
    }

    [Fact]
    public async Task ReplayRejectsTrustedContextForAnotherCustomerBeforeMutation()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-replay-boundary", "key-replay-boundary"));
        await app.FailAsync("job-replay-boundary", "transient", true);
        var otherSecurity = JobTestContext.Security("customer-other");
        var request = new ContractRequest<GetJobStatus>(Vs02ContractNames.GetJobStatus,
            ContractGuard.CurrentVersion, "replay-boundary-request", "replay-boundary-correlation", null,
            otherSecurity, null, new("job-replay-boundary", JobTestContext.CustomerId));

        var denied = await app.ReplayAsync(request);

        Assert.Equal(ContractErrorCategory.AccessDenied, denied.Error?.Category);
        var unchanged = await repository.GetAsync("job-replay-boundary", default);
        Assert.Equal(JobState.Failed, unchanged?.State);
        Assert.Equal(0, unchanged?.ReplayCount);
        var replayed = await app.ReplayAsync(JobTestContext.Status("job-replay-boundary"));
        Assert.Equal(JobState.Scheduled, replayed.Data?.State);
        Assert.Equal(1, (await repository.GetAsync("job-replay-boundary", default))?.ReplayCount);
        Assert.Equal(0, (await repository.GetAsync("job-replay-boundary", default))?.RetryCount);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringExecutionIsDurableAndTerminalCancellationConflicts()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-before", "key-before"));
        Assert.Equal(JobState.Cancelled, (await app.CancelJobAsync(JobTestContext.Cancel("job-before"))).Data?.State);

        await app.ScheduleJobAsync(JobTestContext.Schedule("job-during", "key-during"));
        await app.StartAsync("job-during");
        Assert.Equal(JobState.CancellationRequested,
            (await app.CancelJobAsync(JobTestContext.Cancel("job-during"))).Data?.State);
        Assert.Equal(JobState.Completed, (await app.CompleteAsync("job-during")).State);
        Assert.Equal(ContractErrorCategory.Conflict,
            (await app.CancelJobAsync(JobTestContext.Cancel("job-during"))).Error?.Category);
    }

    [Fact]
    public async Task PendingAutomaticRetryCanBeCancelledAndNeverClaimedOrReplayed()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-pending-retry", "key-pending-retry"));
        await app.ExecuteAsync("job-pending-retry", Policy(new MutableTimeProvider(JobTestContext.Now)),
            Policy(new MutableTimeProvider(JobTestContext.Now)),
            new CountingTarget(JobExecutionResult.Failed("dependency.timeout", true)));

        var cancelled = await app.CancelJobAsync(JobTestContext.Cancel("job-pending-retry"));

        Assert.Equal(JobState.Cancelled, cancelled.Data?.State);
        Assert.False(cancelled.Data?.Retryable);
        var durable = await repository.GetAsync("job-pending-retry", default);
        Assert.Null(durable?.NextAttemptAt);
        Assert.Null(await repository.ClaimAsync("job-pending-retry", "worker", JobTestContext.Now.AddHours(1),
            TimeSpan.FromMinutes(1), default));
        Assert.Equal(ContractErrorCategory.Conflict,
            (await app.ReplayAsync(JobTestContext.Status("job-pending-retry"))).Error?.Category);
    }

    [Fact]
    public async Task CancellationDuringClaimedFailureRequiresReconciliationAndDoesNotRetry()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-cancel-failure", "key-cancel-failure"));
        var lease = await repository.ClaimAsync("job-cancel-failure", "worker", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        await app.CancelJobAsync(JobTestContext.Cancel("job-cancel-failure"));

        var uncertain = await repository.FailClaimAsync(lease!, "dependency.timeout", true,
            JobTestContext.Now.AddMinutes(1), JobTestContext.Now, default);

        Assert.Equal(JobState.CancellationRequested, uncertain.State);
        Assert.False(uncertain.Retryable);
        Assert.Null(uncertain.NextAttemptAt);
        Assert.True(uncertain.ReconciliationRequired);
        Assert.Null(await repository.ClaimAsync(uncertain.JobId, "other-worker", JobTestContext.Now.AddHours(1),
            TimeSpan.FromMinutes(1), default));
        var attemptBefore = Assert.Single(await repository.GetAttemptsAsync(uncertain.JobId, default));
        Assert.Equal("CANCELLATION_OUTCOME_UNKNOWN", attemptBefore.Outcome);
        Assert.NotNull(attemptBefore.EndedAt);
        await repository.ReconcileAsync(uncertain.JobId, JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect, JobTestContext.Now.AddMinutes(2), default);
        Assert.Equal(attemptBefore, Assert.Single(await repository.GetAttemptsAsync(uncertain.JobId, default)));
    }

    [Fact]
    public async Task DefinitiveSuccessAfterCancellationRequestedCompletesKnownOwnerEffect()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-cancel-success", "key-cancel-success"));
        var lease = await repository.ClaimAsync("job-cancel-success", "worker", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        await app.CancelJobAsync(JobTestContext.Cancel("job-cancel-success"));

        var completed = await repository.CompleteClaimAsync(lease!, "known-domain-effect",
            JobTestContext.Now.AddSeconds(1), default);

        Assert.Equal(JobState.Completed, completed.State);
        Assert.Equal("known-domain-effect", completed.OutcomeReference);
        Assert.False(completed.ReconciliationRequired);
    }

    [Fact]
    public async Task ConcurrentClaimHasOneWinnerAndInterruptedLeaseRequiresReconciliation()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-interrupted", "key-interrupted"));
        var claims = await Task.WhenAll(
            repository.ClaimAsync("job-interrupted", "worker-a", JobTestContext.Now, TimeSpan.FromMinutes(1), default),
            repository.ClaimAsync("job-interrupted", "worker-b", JobTestContext.Now, TimeSpan.FromMinutes(1), default));
        Assert.Single(claims, claim => claim is not null);

        Assert.Equal(1, await repository.RecoverInterruptedAsync(JobTestContext.Now.AddMinutes(2), default));
        var recovered = await repository.GetAsync("job-interrupted", default);
        Assert.Equal(JobState.Failed, recovered?.State);
        Assert.True(recovered?.ReconciliationRequired);
        Assert.False(recovered?.Retryable);
        Assert.Equal("job.execution.outcome-unknown", recovered?.FailureCode);
        var attemptBefore = Assert.Single(await repository.GetAttemptsAsync("job-interrupted", default));
        Assert.Equal("OUTCOME_UNKNOWN", attemptBefore.Outcome);
        Assert.Equal("job.execution.outcome-unknown", attemptBefore.FailureCode);
        Assert.NotNull(attemptBefore.EndedAt);
        var reconciled = await repository.ReconcileAsync("job-interrupted", JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect, JobTestContext.Now.AddMinutes(3), default);
        Assert.Equal(JobState.Scheduled, reconciled.State);
        Assert.False(reconciled.ReconciliationRequired);
        Assert.Equal(attemptBefore,
            Assert.Single(await repository.GetAttemptsAsync("job-interrupted", default)));
    }

    [Fact]
    public async Task CancellationRequestedLeaseRecoveryDoesNotClaimDomainCancellationSucceeded()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-cancellation-recovery", "key-cancellation-recovery"));
        Assert.NotNull(await repository.ClaimAsync("job-cancellation-recovery", "worker", JobTestContext.Now,
            TimeSpan.FromMinutes(1), default));
        await app.CancelJobAsync(JobTestContext.Cancel("job-cancellation-recovery"));

        Assert.Equal(1, await repository.RecoverInterruptedAsync(JobTestContext.Now.AddMinutes(2), default));
        var uncertain = await repository.GetAsync("job-cancellation-recovery", default);
        Assert.Equal(JobState.CancellationRequested, uncertain?.State);
        Assert.True(uncertain?.ReconciliationRequired);
        Assert.Equal("job.cancellation.outcome-unknown", uncertain?.FailureCode);
        var attemptBefore = Assert.Single(await repository.GetAttemptsAsync("job-cancellation-recovery", default));
        Assert.Equal("CANCELLATION_OUTCOME_UNKNOWN", attemptBefore.Outcome);
        Assert.Equal("job.cancellation.outcome-unknown", attemptBefore.FailureCode);
        Assert.NotNull(attemptBefore.EndedAt);
        var reconciled = await app.ReconcileAsync("job-cancellation-recovery", JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedCancelled);
        Assert.Equal(JobState.Cancelled, reconciled.State);
        Assert.Equal(attemptBefore,
            Assert.Single(await repository.GetAttemptsAsync("job-cancellation-recovery", default)));
    }

    [Fact]
    public void ReferenceTransportAndPolicyFailClosedOutsideLocalOrCi()
    {
        var production = new ConfigurationManager { ["Monergy:ExecutionZone"] = "PRODUCTION" };
        Assert.Throws<InvalidOperationException>(() => new ReferenceJobExecutionPolicy(production, TimeProvider.System));
    }

    private static JobManagementApplication App(IJobRepository repository, TimeProvider? clock = null) =>
        new(repository, new CapturingTelemetry(), clock ?? new MutableTimeProvider(JobTestContext.Now));

    private static ReferenceJobExecutionPolicy Policy(MutableTimeProvider clock)
    {
        var policy = new ReferenceJobExecutionPolicy(new ConfigurationManager
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL"
        }, clock);
        policy.GrantAuthorization("authorization-d10");
        policy.SetConsent(new("consent-d10", JobTestContext.CustomerId, "DURABLE_PROCESSING",
            clock.Now.AddHours(1), false));
        return policy;
    }

    private static ConfigurationManager ReferenceConfiguration() => new()
    {
        ["Monergy:ExecutionZone"] = "CI_EPHEMERAL"
    };
}
