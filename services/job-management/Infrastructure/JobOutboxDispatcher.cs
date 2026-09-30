using Monergy.Platform;
using Monergy.Contracts;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed class JobOutboxDispatcher(
    IJobRepository repository,
    IGovernedEventTransport<AuditableEvent> transport,
    IEventDeliveryTelemetry telemetry,
    TimeProvider timeProvider)
{
    public bool FailAfterPublishBeforeMarkerOnce { get; set; }

    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var dispatched = 0;
        foreach (var message in await repository.PendingEventsAsync(cancellationToken))
        {
            telemetry.RecordDispatchAttempt();
            try
            {
                await transport.PublishAsync(message, cancellationToken);
                if (FailAfterPublishBeforeMarkerOnce)
                {
                    FailAfterPublishBeforeMarkerOnce = false;
                    throw new IOException("Injected failure after consumer commit and before producer dispatch marker.");
                }
                await repository.MarkDispatchedAsync(message.EventId, timeProvider.GetUtcNow(), cancellationToken);
                dispatched++;
            }
            catch
            {
                telemetry.RecordDispatchFailure();
                throw;
            }
        }
        return dispatched;
    }
}
