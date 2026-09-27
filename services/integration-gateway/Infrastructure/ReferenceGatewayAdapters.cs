using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.IntegrationGateway.Application;
using Monergy.Services.IntegrationGateway.Domain;

namespace Monergy.Services.IntegrationGateway.Infrastructure;

public sealed class ReferenceConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IProviderConnector> connectors = new(StringComparer.Ordinal);
    private readonly object sync = new();

    public ReferenceConnectorRegistry(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    public void Register(IProviderConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        lock (sync)
        {
            if (!connectors.TryAdd(connector.ConnectorId, connector))
            {
                throw new InvalidOperationException($"Connector '{connector.ConnectorId}' is already registered.");
            }
        }
    }

    public IProviderConnector? Find(string connectorId)
    {
        lock (sync) { return connectors.GetValueOrDefault(connectorId); }
    }

    public ImmutableArray<IProviderConnector> All()
    {
        lock (sync) { return connectors.Values.OrderBy(item => item.ConnectorId, StringComparer.Ordinal).ToImmutableArray(); }
    }
}

public sealed class ReferenceProviderConnector : IProviderConnector
{
    private readonly ConcurrentQueue<ConnectorAttemptResult> outcomes = [];

    public ReferenceProviderConnector(string connectorId, IEnumerable<string> supportedOperations, IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        ConnectorId = connectorId;
        SupportedOperations = supportedOperations.ToImmutableHashSet(StringComparer.Ordinal);
    }

    public string ConnectorId { get; }
    public ImmutableHashSet<string> SupportedOperations { get; }
    public int ExecutionCount { get; private set; }
    public Action<int>? AfterExecution { get; set; }

    public void Enqueue(ConnectorAttemptResult result) => outcomes.Enqueue(result);

    public Task<ConnectorAttemptResult> ExecuteAsync(ConnectorExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionCount++;
        var result = outcomes.TryDequeue(out var configured)
            ? configured
            : ConnectorAttemptResult.Success($"canonical-result-{request.ProviderOperationId}");
        AfterExecution?.Invoke(ExecutionCount);
        return Task.FromResult(result);
    }
}

public sealed record ReferenceAuthorizationGrant(
    TrustedSecurityContext Context,
    DateTimeOffset AuthorizationExpiresAt,
    bool Revoked);

// This adapter consumes CID-007 evidence; it does not treat trusted context as blanket authorization.
public sealed class ReferenceGatewayAuthorizationPolicy : IGatewayAuthorizationPolicy
{
    private readonly Dictionary<string, ReferenceAuthorizationGrant> grants = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly TimeProvider clock;

    public ReferenceGatewayAuthorizationPolicy(IConfiguration configuration, TimeProvider clock)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        this.clock = clock;
    }

    public int EvaluationCount { get; private set; }

    public void SetGrant(ReferenceAuthorizationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (sync) { grants[grant.Context.Access.AuthorizationContextId] = grant; }
    }

    public Task<ContractError?> AuthorizeAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            EvaluationCount++;
            ContractErrorCategory? category = null;
            if (!grants.TryGetValue(context.Access.AuthorizationContextId, out var grant) ||
                grant.Context != context || context.Access.CustomerId != customerId ||
                grant.Revoked || grant.AuthorizationExpiresAt <= clock.GetUtcNow())
            {
                category = ContractErrorCategory.AccessDenied;
            }

            var error = category is null ? null : new ContractError(
                "integration.authorization.denied", category.Value, "Current authorization is required.", false, correlationId);
            return Task.FromResult(error);
        }
    }
}

public sealed record ReferenceConsentDecision(
    string ConsentReferenceId,
    string CustomerId,
    string Purpose,
    DateTimeOffset ExpiresAt,
    bool Revoked);

// This adapter consumes the current CID-011 decision; it does not own consent meaning or lifecycle.
public sealed class ReferenceConsentDecisionPort : IConsentDecisionPort
{
    private readonly Dictionary<string, ReferenceConsentDecision> decisions = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly TimeProvider clock;

    public ReferenceConsentDecisionPort(IConfiguration configuration, TimeProvider clock)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        this.clock = clock;
    }

    public int EvaluationCount { get; private set; }

    public void SetDecision(ReferenceConsentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (sync) { decisions[decision.ConsentReferenceId] = decision; }
    }

    public Task<ContractError?> EvaluateAsync(TrustedSecurityContext context, string customerId, string operation,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            EvaluationCount++;
            ContractErrorCategory? category = null;
            var reference = context.Access.ConsentReferenceId;
            if (string.IsNullOrWhiteSpace(reference))
            {
                category = ContractErrorCategory.ConsentRequired;
            }
            else if (!decisions.TryGetValue(reference, out var decision) ||
                decision.CustomerId != customerId || decision.Purpose != context.Access.Purpose)
            {
                category = ContractErrorCategory.ConsentRequired;
            }
            else if (decision.Revoked)
            {
                category = ContractErrorCategory.ConsentRevoked;
            }
            else if (decision.ExpiresAt <= clock.GetUtcNow())
            {
                category = ContractErrorCategory.ConsentExpired;
            }

            var error = category is null ? null : new ContractError(
                "integration.consent.denied", category.Value, "Current purpose-bound consent is required.", false, correlationId);
            return Task.FromResult(error);
        }
    }
}

public sealed class InMemoryGatewayOperationRepository : IGatewayOperationRepository
{
    private sealed record Entry(string Fingerprint, Lazy<Task<GatewayExecution>> Execution);

    private readonly ConcurrentDictionary<GatewayRequestIdentity, Entry> requests = [];
    private readonly ConcurrentDictionary<string, GatewayExecution> results = new(StringComparer.Ordinal);

    public InMemoryGatewayOperationRepository(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);

    public async Task<IdempotentGatewayExecution> ExecuteAsync(GatewayRequestIdentity identity, string fingerprint,
        Func<Task<GatewayExecution>> transition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var candidate = new Entry(fingerprint, new Lazy<Task<GatewayExecution>>(async () =>
        {
            var execution = await transition().ConfigureAwait(false);
            results[execution.Result.ProviderOperationId] = execution;
            return execution;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        var selected = requests.GetOrAdd(identity, candidate);
        if (!string.Equals(selected.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new GatewayException("integration.idempotency.conflict", ContractErrorCategory.Conflict,
                "The idempotency identity was reused with different request content.");
        }

        var first = ReferenceEquals(selected, candidate);
        var concurrent = !first && selected.Execution.IsValueCreated && !selected.Execution.Value.IsCompleted;
        var execution = await selected.Execution.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(execution, first ? IdempotencyDisposition.FirstExecution
            : concurrent ? IdempotencyDisposition.ConcurrentReplay : IdempotencyDisposition.Replay);
    }

    public GatewayExecution? Find(string providerOperationId, string customerId)
    {
        var result = results.GetValueOrDefault(providerOperationId);
        return result?.Result.CustomerId == customerId ? result : null;
    }
}

public sealed class InMemoryGatewayEventSink : IGatewayEventSink
{
    private readonly ConcurrentDictionary<string, object> messages = new(StringComparer.Ordinal);
    public InMemoryGatewayEventSink(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);
    public ImmutableArray<object> Messages => messages.Values.OrderBy(value => value.GetType().Name, StringComparer.Ordinal).ToImmutableArray();

    public Task PublishAsync(DomainEvent<ProviderResultReceivedPayload> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        messages.TryAdd(message.EventId, message);
        return Task.CompletedTask;
    }

    public Task PublishAsync(DomainEvent<ProviderOperationFailedPayload> message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        messages.TryAdd(message.EventId, message);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGatewayTelemetry : IGatewayTelemetry
{
    public InMemoryGatewayTelemetry(IConfiguration configuration) => ReferenceAdapterGuard.EnsureAllowed(configuration);
    public ConcurrentQueue<GatewayTelemetrySignal> Signals { get; } = [];
    public void Record(GatewayTelemetrySignal signal) => Signals.Enqueue(signal);
}

public sealed class ConnectorRuntimeState(GatewayExecutionOptions options) : IConnectorRuntimeState
{
    private sealed record State(int ConsecutiveFailures, long TotalAttempts, DateTimeOffset? LastAttemptAt, string? LastFailureCode);
    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.Ordinal);

    public bool CanExecute(string connectorId) =>
        (states.GetValueOrDefault(connectorId)?.ConsecutiveFailures ?? 0) < options.CircuitFailureThreshold;

    public void Record(string connectorId, ConnectorAttemptResult result, DateTimeOffset occurredAt)
    {
        states.AddOrUpdate(connectorId,
            _ => Next(new State(0, 0, null, null), result, occurredAt),
            (_, current) => Next(current, result, occurredAt));
    }

    public ConnectorHealth GetHealth(string connectorId)
    {
        var state = states.GetValueOrDefault(connectorId) ?? new State(0, 0, null, null);
        var health = state.ConsecutiveFailures >= options.CircuitFailureThreshold ? ConnectorHealthState.Unavailable
            : state.ConsecutiveFailures > 0 ? ConnectorHealthState.Degraded : ConnectorHealthState.Healthy;
        return new(connectorId, health, state.ConsecutiveFailures, state.TotalAttempts, state.LastAttemptAt, state.LastFailureCode);
    }

    private static State Next(State current, ConnectorAttemptResult result, DateTimeOffset occurredAt) =>
        result.Outcome == ConnectorAttemptOutcome.Succeeded
            ? new(0, current.TotalAttempts + 1, occurredAt, null)
            : new(current.ConsecutiveFailures + 1, current.TotalAttempts + 1, occurredAt, result.FailureCode);
}
