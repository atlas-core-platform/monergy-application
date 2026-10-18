using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed class EvidenceScannerHealthCheck(ClamdEvidenceMalwareScanner scanner) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await scanner.ReadyAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy("Local upload scanner metadata verified.")
            : HealthCheckResult.Unhealthy("Upload scanner unavailable, incompatible or signature database stale.");
}
