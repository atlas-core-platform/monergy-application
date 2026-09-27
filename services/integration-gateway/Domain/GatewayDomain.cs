using System.Collections.Immutable;
using Monergy.Contracts;

namespace Monergy.Services.IntegrationGateway.Domain;

public enum ConnectorAttemptOutcome
{
    Succeeded,
    TransientFailure,
    PermanentFailure,
    RateLimited,
    TemporarilyUnavailable,
    MalformedProviderResponse,
    UnknownOutcome,
    CircuitRejected,
}

public enum ConnectorHealthState
{
    Healthy,
    Degraded,
    Unavailable,
}

public enum IdempotencyDisposition
{
    FirstExecution,
    Replay,
    ConcurrentReplay,
}

public sealed record ConnectorExecutionRequest(
    string ProviderOperationId,
    string CustomerId,
    string Operation,
    ImmutableArray<ProviderRequestField> Fields,
    string CorrelationId,
    int Attempt);

public sealed record ConnectorAttemptResult(
    ConnectorAttemptOutcome Outcome,
    string? CanonicalResultReferenceId,
    string? FailureCode,
    bool Retryable)
{
    public static ConnectorAttemptResult Success(string referenceId) =>
        new(ConnectorAttemptOutcome.Succeeded, referenceId, null, false);

    public static ConnectorAttemptResult Failure(ConnectorAttemptOutcome outcome, string code, bool retryable) =>
        new(outcome, null, code, retryable);
}

public sealed record GatewayRequestIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey);

public sealed record GatewayExecution(ProviderOperationResult Result);

public sealed record IdempotentGatewayExecution(
    GatewayExecution Execution,
    IdempotencyDisposition Disposition);

public sealed record ConnectorHealth(
    string ConnectorId,
    ConnectorHealthState State,
    int ConsecutiveFailures,
    long TotalAttempts,
    DateTimeOffset? LastAttemptAt,
    string? LastFailureCode);

public sealed class GatewayException(
    string code,
    ContractErrorCategory category,
    string message,
    bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public ContractErrorCategory Category { get; } = category;
    public bool Retryable { get; } = retryable;
}
