using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.JobManagement.Tests;

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now += duration;
}

internal sealed class CapturingTelemetry : ILifecycleTelemetry
{
    public List<LifecycleSignal> Signals { get; } = [];
    public void Record(LifecycleSignal signal) => Signals.Add(signal);
}

internal static class JobTestContext
{
    public const string CustomerId = "customer-d10";
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    public static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
            ["Monergy:Persistence:JobManagement:RuntimeConnection"] = Environment.GetEnvironmentVariable("D10_JOB_MANAGEMENT_RUNTIME_CONNECTION"),
            ["Monergy:Persistence:Reporting:RuntimeConnection"] = Environment.GetEnvironmentVariable("D10_REPORTING_RUNTIME_CONNECTION"),
            ["Monergy:Persistence:Audit:RuntimeConnection"] = Environment.GetEnvironmentVariable("D10_AUDIT_RUNTIME_CONNECTION"),
        }).Build();

    public static TrustedSecurityContext Security(string customerId = CustomerId,
        string authorization = "authorization-d10", string consent = "consent-d10") => new(
        new("actor-d10", "CUSTOMER", Now, "authentication-d10"),
        new("job-management", "workload-d10"),
        new("DURABLE_PROCESSING", consent, authorization, customerId));

    public static ContractRequest<ScheduleJob> Schedule(string jobId, string key,
        string payload = "processing-d10", TrustedSecurityContext? security = null) => new(
        Vs02ContractNames.ScheduleJob, ContractGuard.CurrentVersion, $"request-{jobId}",
        $"correlation-{jobId}", null, security ?? Security(), key,
        new(jobId, Vs02ContractNames.ProcessDocument, "Document Intelligence Service", payload,
            (security ?? Security()).Access.CustomerId, Now));

    public static ContractRequest<GetJobStatus> Status(string jobId, TrustedSecurityContext? security = null) => new(
        Vs02ContractNames.GetJobStatus, ContractGuard.CurrentVersion, $"status-{jobId}",
        $"correlation-{jobId}", null, security ?? Security(), null,
        new(jobId, (security ?? Security()).Access.CustomerId));

    public static ContractRequest<CancelJob> Cancel(string jobId, TrustedSecurityContext? security = null) => new(
        Vs02ContractNames.CancelJob, ContractGuard.CurrentVersion, $"cancel-{jobId}",
        $"correlation-{jobId}", null, security ?? Security(), $"cancel-key-{jobId}",
        new(jobId, (security ?? Security()).Access.CustomerId, "customer-requested"));
}

internal sealed class CountingTarget(JobExecutionResult result) : IJobExecutionTarget
{
    public int Calls { get; private set; }
    public Task<JobExecutionResult> ExecuteAsync(DurableJobRecord job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(result);
    }
}
