using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Monergy.Platform;

public sealed record LifecycleSignal(
    string Service,
    string Operation,
    string Outcome,
    string RequestId,
    string CorrelationId,
    string? CausationId,
    string SubjectType,
    string SubjectId,
    string AdapterKind);

public interface ILifecycleTelemetry
{
    void Record(LifecycleSignal signal);
}

public sealed class OpenTelemetryLifecycleTelemetry : ILifecycleTelemetry
{
    public const string SourceName = "Monergy.VS02";

    private static readonly ActivitySource ActivitySource = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> OperationCounter = Meter.CreateCounter<long>("monergy.vs02.operations");

    public void Record(LifecycleSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        using var activity = ActivitySource.StartActivity(signal.Operation, ActivityKind.Internal);
        activity?.SetTag("monergy.service", signal.Service);
        activity?.SetTag("monergy.operation", signal.Operation);
        activity?.SetTag("monergy.outcome", signal.Outcome);
        activity?.SetTag("monergy.request_id", signal.RequestId);
        activity?.SetTag("monergy.correlation_id", signal.CorrelationId);
        activity?.SetTag("monergy.causation_id", signal.CausationId);
        activity?.SetTag("monergy.subject_type", signal.SubjectType);
        activity?.SetTag("monergy.subject_id", signal.SubjectId);
        activity?.SetTag("monergy.adapter_kind", signal.AdapterKind);

        OperationCounter.Add(
            1,
            new KeyValuePair<string, object?>("monergy.service", signal.Service),
            new KeyValuePair<string, object?>("monergy.operation", signal.Operation),
            new KeyValuePair<string, object?>("monergy.outcome", signal.Outcome),
            new KeyValuePair<string, object?>("monergy.adapter_kind", signal.AdapterKind));
    }
}
