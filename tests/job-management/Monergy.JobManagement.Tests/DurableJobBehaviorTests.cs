using Microsoft.Extensions.Configuration;
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
    public async Task ExecutionReevaluatesAuthorizationAndConsentEveryAttempt()
    {
        var repository = new InMemoryJobRepository();
        var clock = new MutableTimeProvider(JobTestContext.Now);
        var app = App(repository, clock);
        var policy = Policy(clock);
        var target = new CountingTarget(JobExecutionResult.Failed("dependency.timeout.unknown", true));
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-retry", "key-retry"));

        var first = await app.ExecuteAsync("job-retry", policy, policy, target);
        Assert.Equal(ContractOutcome.Failed, first.Outcome);
        Assert.True(first.Error?.Retryable);
        await app.ReplayAsync(JobTestContext.Status("job-retry"));
        policy.SetConsent(new("consent-d10", JobTestContext.CustomerId, "DURABLE_PROCESSING",
            clock.Now.AddHours(1), true));
        var denied = await app.ExecuteAsync("job-retry", policy, policy, target);

        Assert.Equal(ContractErrorCategory.ConsentRevoked, denied.Error?.Category);
        Assert.Equal(1, target.Calls);
        Assert.Equal(2, policy.AuthorizationEvaluations);
        Assert.Equal(2, policy.ConsentEvaluations);
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
    public async Task InterruptedRunningJobIsRecoveredForSafeRetry()
    {
        var repository = new InMemoryJobRepository();
        var app = App(repository);
        await app.ScheduleJobAsync(JobTestContext.Schedule("job-interrupted", "key-interrupted"));
        await app.StartAsync("job-interrupted");

        Assert.Equal(1, await app.RecoverInterruptedAsync());
        var recovered = await repository.GetAsync("job-interrupted", default);
        Assert.Equal(JobState.Scheduled, recovered?.State);
        Assert.True(recovered?.Retryable);
        Assert.Equal("job.execution.interrupted", recovered?.FailureCode);
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
}
