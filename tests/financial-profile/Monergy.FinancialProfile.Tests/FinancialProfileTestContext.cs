using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.FinancialProfile.Tests;

internal static class FinancialProfileTestContext
{
    public const string CustomerId = "customer-001";
    public const string CorrelationId = "correlation-001";

    public static TrustedSecurityContext Security(string customerId = CustomerId) =>
        new(
            new ActorContext("actor-001", "CUSTOMER", DateTimeOffset.Parse("2026-09-13T00:00:00Z", CultureInfo.InvariantCulture), "authn-001"),
            new WorkloadContext("financial-profile-tests", "workload-identity-001"),
            new AccessContext("financial-profile-authority", "consent-001", "authorization-001", customerId));

    public static ContractRequest<T> Request<T>(
        string name,
        T payload,
        string requestId,
        string? idempotencyKey = null,
        TrustedSecurityContext? security = null) =>
        new(name, ContractGuard.CurrentVersion, requestId, CorrelationId, "cause-001", security ?? Security(), idempotencyKey, payload);
}

internal sealed class CapturingTelemetry : ILifecycleTelemetry
{
    public List<LifecycleSignal> Signals { get; } = [];

    public void Record(LifecycleSignal signal) => Signals.Add(signal);
}

internal static class AuditEventMapper
{
    public static AuditableEvent From<TPayload>(DomainEvent<TPayload> source) =>
        new(
            source.ContractId,
            source.EventId,
            source.EventName,
            source.EventVersion,
            source.OccurredAt,
            source.CorrelationId,
            source.CausationId,
            source.Producer,
            source.SubjectType,
            source.SubjectId);
}
