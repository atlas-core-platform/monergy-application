using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;

namespace Monergy.Services.Reporting.Infrastructure;

public sealed class ReportingAuditOutboxDispatcher(
    IReportRepository repository,
    IGovernedEventTransport<AuditableEvent> transport,
    IEventDeliveryTelemetry telemetry)
{
    public bool FailAfterPublishBeforeMarkerOnce { get; set; }

    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var dispatched = 0;
        foreach (var source in repository.PendingEvents())
        {
            var message = new AuditableEvent(source.ContractId, source.EventId, source.EventName,
                source.EventVersion, source.OccurredAt, source.CorrelationId, source.CausationId,
                source.Producer, source.SubjectType, source.SubjectId);
            telemetry.RecordDispatchAttempt();
            try
            {
                await transport.PublishAsync(message, cancellationToken);
                if (FailAfterPublishBeforeMarkerOnce)
                {
                    FailAfterPublishBeforeMarkerOnce = false;
                    throw new IOException("Injected failure after Audit commit and before Reporting dispatch marker.");
                }
                repository.AcknowledgeEvent(source.EventId);
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
