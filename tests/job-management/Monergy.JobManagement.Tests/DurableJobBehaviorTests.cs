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
        Assert.Equal(JobState.Cancelled, (await app.CompleteAsync("job-during")).State);
        Assert.Equal(ContractErrorCategory.Conflict,
            (await app.CancelJobAsync(JobTestContext.Cancel("job-during"))).Error?.Category);
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
        var reconciled = await repository.ReconcileAsync("job-interrupted", JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedNoEffect, JobTestContext.Now.AddMinutes(3), default);
        Assert.Equal(JobState.Scheduled, reconciled.State);
        Assert.False(reconciled.ReconciliationRequired);
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
        var reconciled = await app.ReconcileAsync("job-cancellation-recovery", JobTestContext.CustomerId,
            JobReconciliationOutcome.ConfirmedCancelled);
        Assert.Equal(JobState.Cancelled, reconciled.State);
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
