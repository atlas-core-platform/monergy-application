using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Monergy.Services.Evidence.Infrastructure;

public sealed class EvidenceScannerHealthCheck(ClamdEvidenceMalwareScanner scanner) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        (await scanner.ProbeAsync(cancellationToken).ConfigureAwait(false)) switch
        {
            ClamdScannerReadiness.Ready => HealthCheckResult.Healthy("Local upload scanner metadata verified."),
            ClamdScannerReadiness.StaleDatabase => HealthCheckResult.Unhealthy("Upload scanner signatures exceed the approved 72-hour freshness limit."),
            ClamdScannerReadiness.IncompatibleEngine => HealthCheckResult.Unhealthy("Upload scanner engine differs from the pinned approved version."),
            ClamdScannerReadiness.InvalidMetadata => HealthCheckResult.Unhealthy("Upload scanner metadata could not be validated."),
            ClamdScannerReadiness.TimedOut => HealthCheckResult.Unhealthy("Upload scanner metadata probe timed out."),
            _ => HealthCheckResult.Unhealthy("Upload scanner is unavailable or returned an invalid protocol response.")
        };
}
