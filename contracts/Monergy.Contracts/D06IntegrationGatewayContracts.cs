using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Monergy.Contracts;

public static class IntegrationGatewayContractNames
{
    public const string ExecuteProviderRequest = "ExecuteProviderRequest";
    public const string GetProviderOperationStatus = "GetProviderOperationStatus";
    public const string ProviderResultReceived = "ProviderResultReceived";
    public const string ProviderOperationFailed = "ProviderOperationFailed";
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderOperationState>))]
public enum ProviderOperationState
{
    Succeeded,
    Failed,
    RateLimited,
    UnknownOutcome,
}

public sealed record ProviderRequestField(string Name, string Value);

public sealed record ExecuteProviderRequest(
    string CustomerId,
    string ConnectorId,
    string Operation,
    ImmutableArray<ProviderRequestField> Fields);

public sealed record GetProviderOperationStatus(string CustomerId, string ProviderOperationId);

public sealed record ProviderOperationResult(
    string ProviderOperationId,
    string CustomerId,
    string ConnectorId,
    string Operation,
    ProviderOperationState State,
    int AttemptCount,
    string? CanonicalResultReferenceId,
    ContractError? Failure,
    DateTimeOffset UpdatedAt);

public sealed record ProviderResultReceivedPayload(
    string ProviderOperationId,
    string CustomerId,
    string ConnectorId,
    string Operation,
    string CanonicalResultReferenceId,
    int AttemptCount);

public sealed record ProviderOperationFailedPayload(
    string ProviderOperationId,
    string CustomerId,
    string ConnectorId,
    string Operation,
    string FailureCode,
    ContractErrorCategory FailureCategory,
    bool Retryable,
    ProviderOperationState State,
    int AttemptCount);
