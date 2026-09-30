using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public interface IJobExecutionTargetResolver
{
    IJobExecutionTarget? Resolve(DurableJobRecord job);
}

public sealed class ReferenceJobExecutionTargetRegistry : IJobExecutionTargetResolver
{
    private readonly Dictionary<(string Owner, string Job), IJobExecutionTarget> targets = [];
    private readonly object sync = new();

    public void Register(string ownerService, string jobName, IJobExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (sync) targets[(ownerService, jobName)] = target;
    }

    public IJobExecutionTarget? Resolve(DurableJobRecord job)
    {
        lock (sync) return targets.GetValueOrDefault((job.OwnerService, job.JobName));
    }
}

public sealed partial class DurableJobExecutionWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly IJobRepository repository;
    private readonly JobManagementApplication application;
    private readonly IJobAuthorizationPolicy authorization;
    private readonly IJobConsentDecisionPort consent;
    private readonly IJobExecutionTargetResolver targets;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DurableJobExecutionWorker> logger;
    private readonly string workerId = $"job-worker-{Guid.NewGuid():N}";

    public DurableJobExecutionWorker(IConfiguration configuration, IJobRepository repository,
        JobManagementApplication application, IJobAuthorizationPolicy authorization,
        IJobConsentDecisionPort consent, IJobExecutionTargetResolver targets,
        TimeProvider timeProvider, ILogger<DurableJobExecutionWorker> logger)
    {
        D10ReferenceTransportGuard.EnsureAllowed(configuration);
        this.repository = repository;
        this.application = application;
        this.authorization = authorization;
        this.consent = consent;
        this.targets = targets;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public Task<bool> RunOnceAsync(CancellationToken cancellationToken = default) =>
        RunOnceAsync(null, cancellationToken);

    public Task<bool> RunJobOnceAsync(string jobId, CancellationToken cancellationToken = default) =>
        RunOnceAsync(jobId, cancellationToken);

    private async Task<bool> RunOnceAsync(string? jobId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await repository.RecoverInterruptedAsync(now, cancellationToken);
        var lease = jobId is null
            ? await repository.ClaimNextAsync(workerId, now, LeaseDuration, cancellationToken)
            : await repository.ClaimAsync(jobId, workerId, now, LeaseDuration, cancellationToken);
        if (lease is null) return false;
        var target = targets.Resolve(lease.Job);
        if (target is null)
        {
            await repository.FailClaimAsync(lease, "job.execution.target-unavailable", true,
                now.AddMinutes(1), now, cancellationToken);
            return true;
        }
        await application.ExecuteClaimedAsync(lease, authorization, consent, target, cancellationToken);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogExecutionCycleFailure(logger, error);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable Job Management execution cycle failed closed.")]
    private static partial void LogExecutionCycleFailure(ILogger logger, Exception error);
}

public sealed partial class JobOutboxDispatchWorker : BackgroundService
{
    private readonly JobOutboxDispatcher dispatcher;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JobOutboxDispatchWorker> logger;

    public JobOutboxDispatchWorker(IConfiguration configuration, JobOutboxDispatcher dispatcher,
        TimeProvider timeProvider, ILogger<JobOutboxDispatchWorker> logger)
    {
        D10ReferenceTransportGuard.EnsureAllowed(configuration);
        this.dispatcher = dispatcher;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default) =>
        dispatcher.DispatchAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken) == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                LogDispatchCycleFailure(logger, error);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job Management outbox dispatch cycle failed closed.")]
    private static partial void LogDispatchCycleFailure(ILogger logger, Exception error);
}
