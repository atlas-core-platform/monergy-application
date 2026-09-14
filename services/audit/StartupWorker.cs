using Monergy.Platform;

namespace Monergy.Services.Audit;

public sealed partial class StartupWorker(ILogger<StartupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, "Audit Service", ComponentMetadata.Vs02CandidateState);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "{ServiceName} started in {ImplementationState} state.")]
    private static partial void LogStarted(ILogger logger, string serviceName, string implementationState);
}
