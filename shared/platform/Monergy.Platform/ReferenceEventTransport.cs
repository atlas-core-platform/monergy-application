using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public sealed record EventDeliveryResult(int Consumers, int Created, int Duplicates);

public sealed record EventDeliveryTelemetrySnapshot(
    int DispatchAttempts,
    int DispatchFailures,
    int DuplicateDeliveries,
    int AuditIngestionFailures);

public interface IEventDeliveryTelemetry
{
    void RecordDispatchAttempt();
    void RecordDispatchFailure();
    void RecordDuplicateDeliveries(int count);
    void RecordAuditIngestionFailure();
    EventDeliveryTelemetrySnapshot Snapshot();
}

public sealed class EventDeliveryTelemetry : IEventDeliveryTelemetry
{
    private int dispatchAttempts;
    private int dispatchFailures;
    private int duplicateDeliveries;
    private int auditIngestionFailures;

    public void RecordDispatchAttempt() => Interlocked.Increment(ref dispatchAttempts);
    public void RecordDispatchFailure() => Interlocked.Increment(ref dispatchFailures);
    public void RecordDuplicateDeliveries(int count) => Interlocked.Add(ref duplicateDeliveries, count);
    public void RecordAuditIngestionFailure() => Interlocked.Increment(ref auditIngestionFailures);

    public EventDeliveryTelemetrySnapshot Snapshot() => new(
        Volatile.Read(ref dispatchAttempts),
        Volatile.Read(ref dispatchFailures),
        Volatile.Read(ref duplicateDeliveries),
        Volatile.Read(ref auditIngestionFailures));
}

public interface IGovernedEventConsumer<in TEvent> where TEvent : class
{
    Task<bool> ConsumeAsync(TEvent source, CancellationToken cancellationToken);
}

public interface IGovernedEventTransport<in TEvent> where TEvent : class
{
    Task<EventDeliveryResult> PublishAsync(TEvent source, CancellationToken cancellationToken);
}

public static class D10ReferenceTransportGuard
{
    private static readonly HashSet<string> AllowedZones =
        new(StringComparer.OrdinalIgnoreCase) { "LOCAL", "CI_EPHEMERAL", "CI/EPHEMERAL" };

    public static void EnsureAllowed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var zone = configuration["Monergy:ExecutionZone"] ?? string.Empty;
        if (!AllowedZones.Contains(zone))
            throw new InvalidOperationException("D10 reference event transport may run only in LOCAL or CI_EPHEMERAL.");
    }
}

public sealed class ReferenceGovernedEventTransport<TEvent> : IGovernedEventTransport<TEvent>
    where TEvent : class
{
    private readonly IGovernedEventConsumer<TEvent>[] consumers;
    private readonly IEventDeliveryTelemetry telemetry;

    public ReferenceGovernedEventTransport(IConfiguration configuration,
        IEnumerable<IGovernedEventConsumer<TEvent>> consumers,
        IEventDeliveryTelemetry telemetry)
    {
        D10ReferenceTransportGuard.EnsureAllowed(configuration);
        this.consumers = consumers.ToArray();
        this.telemetry = telemetry;
    }

    public async Task<EventDeliveryResult> PublishAsync(TEvent source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (consumers.Length == 0)
            throw new InvalidOperationException("The reference event transport has no governed consumer.");
        var created = 0;
        try
        {
            foreach (var consumer in consumers)
                if (await consumer.ConsumeAsync(source, cancellationToken)) created++;
            var duplicates = consumers.Length - created;
            telemetry.RecordDuplicateDeliveries(duplicates);
            return new(consumers.Length, created, duplicates);
        }
        catch
        {
            telemetry.RecordAuditIngestionFailure();
            throw;
        }
    }
}
