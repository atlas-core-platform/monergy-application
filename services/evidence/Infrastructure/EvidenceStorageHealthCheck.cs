using Microsoft.Extensions.Diagnostics.HealthChecks;
namespace Monergy.Services.Evidence.Infrastructure;
public sealed class EvidenceStorageHealthCheck(TenantEvidenceRepository repository, S3EvidenceContentStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await repository.CheckReadyAsync(deadline.Token);
            await store.CheckReadyAsync(deadline.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { return HealthCheckResult.Unhealthy("Configured Evidence database or versioned storage is unavailable."); }
    }
}
