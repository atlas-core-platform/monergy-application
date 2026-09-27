using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Services.IntegrationGateway.Domain;

namespace Monergy.Services.IntegrationGateway.Application;

public interface IProviderConnector
{
    string ConnectorId { get; }
    ImmutableHashSet<string> SupportedOperations { get; }
    Task<ConnectorAttemptResult> ExecuteAsync(ConnectorExecutionRequest request, CancellationToken cancellationToken);
}

public interface IConnectorRegistry
{
    IProviderConnector? Find(string connectorId);
    ImmutableArray<IProviderConnector> All();
}

public interface IGatewayAuthorizationPolicy
{
    Task<ContractError?> AuthorizeAsync(
        TrustedSecurityContext context,
        string customerId,
        string operation,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface IConsentDecisionPort
{
    Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext context,
        string customerId,
        string operation,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface IGatewayOperationRepository
{
    Task<IdempotentGatewayExecution> ExecuteAsync(
        GatewayRequestIdentity identity,
        string fingerprint,
        Func<Task<GatewayExecution>> transition,
        CancellationToken cancellationToken);

    GatewayExecution? Find(string providerOperationId, string customerId);
}

public interface IGatewayEventSink
{
    Task PublishAsync(DomainEvent<ProviderResultReceivedPayload> message, CancellationToken cancellationToken);
    Task PublishAsync(DomainEvent<ProviderOperationFailedPayload> message, CancellationToken cancellationToken);
}

public sealed record GatewayTelemetrySignal(
    string Name,
    string ConnectorId,
    string Operation,
    string Outcome,
    int Attempt,
    string CorrelationId,
    string? ProviderOperationId);

public interface IGatewayTelemetry
{
    void Record(GatewayTelemetrySignal signal);
}

public interface IConnectorRuntimeState
{
    bool CanExecute(string connectorId);
    void Record(string connectorId, ConnectorAttemptResult result, DateTimeOffset occurredAt);
    ConnectorHealth GetHealth(string connectorId);
}

public sealed record GatewayExecutionOptions(int MaximumAttempts = 2, int CircuitFailureThreshold = 2);
