using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed record AuthenticationReference(string Value, string CustomerId);

public sealed record AuthenticationResolution(
    ActorContext? Actor,
    string? CustomerId,
    string? FailureCode,
    ContractErrorCategory? FailureCategory,
    bool Retryable)
{
    public static AuthenticationResolution Succeeded(ActorContext actor, string customerId) =>
        new(actor, customerId, null, null, false);

    public static AuthenticationResolution Failed(
        string code,
        ContractErrorCategory category,
        bool retryable = false) =>
        new(null, null, code, category, retryable);
}

public interface ICustomerAuthenticationProvider
{
    Task<AuthenticationResolution> ResolveAsync(
        AuthenticationReference authentication,
        CancellationToken cancellationToken);
}

public sealed record TrustedSessionPolicy(TimeSpan Lifetime)
{
    public static TrustedSessionPolicy ReferenceDefault { get; } = new(TimeSpan.FromMinutes(30));
}

public sealed record TrustedSessionSnapshot(
    string CustomerId,
    ActorContext Actor,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    DateTimeOffset? RevokedAt,
    int Revision);

public enum TrustedSessionEvaluationStatus
{
    Current,
    Expired,
    Revoked,
    Mismatch,
    Unverifiable,
    DependencyFailure,
}

public sealed record TrustedSessionEvaluation(
    TrustedSessionEvaluationStatus Status,
    TrustedSessionSnapshot? Session,
    bool Created);

public enum TrustedSessionRevocationStatus
{
    Revoked,
    AlreadyRevoked,
    NotCurrent,
    DependencyFailure,
}

public sealed record TrustedSessionRevocation(
    TrustedSessionRevocationStatus Status,
    TrustedSessionSnapshot? Session);

public interface ITrustedSessionRepository
{
    TrustedSessionEvaluation EstablishOrEvaluate(
        string customerId,
        ActorContext actor,
        DateTimeOffset now,
        TimeSpan lifetime);

    TrustedSessionRevocation Revoke(
        string authenticationContextId,
        string customerId,
        string actorId,
        DateTimeOffset now);
}

public interface ICustomerIdentityEventSink
{
    void Publish(DomainEvent<CustomerIdentityChangedPayload> message);
}

public sealed record CustomerMutationIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey);

public sealed record KycMutationIdentity(
    string ContractName,
    string ContractVersion,
    string CustomerId,
    string IdempotencyKey);

public enum RepositoryFailure
{
    None,
    NotFound,
    Conflict,
    DuplicateRequest,
    DependencyFailure,
}

public enum MutationDisposition
{
    Created,
    Updated,
    Replayed,
}

public sealed record RepositoryRead<T>(T? Value, RepositoryFailure Failure);

public sealed record CustomerMutation(
    CustomerProjection? Value,
    MutationDisposition Disposition,
    RepositoryFailure Failure);

public sealed record KycMutation(
    KycVerification? Value,
    MutationDisposition Disposition,
    RepositoryFailure Failure);

public interface ICustomerIdentityRepository
{
    RepositoryRead<CustomerProjection> GetCustomer(string customerId);

    CustomerMutation RegisterOrUpdateCustomer(
        CustomerMutationIdentity identity,
        string payloadFingerprint,
        RegisterOrUpdateCustomer payload,
        DateTimeOffset recordedAt);

    KycMutation RecordKycResult(
        KycMutationIdentity identity,
        string payloadFingerprint,
        RecordKycResult payload,
        DateTimeOffset recordedAt);

    IReadOnlyList<KycVerification> GetKycHistory(string customerId);
}

public sealed record CustomerIdentityTelemetrySignal(
    string Operation,
    string Outcome,
    string CustomerId,
    string RequestId,
    string CorrelationId);

public interface ICustomerIdentityTelemetry
{
    void Record(CustomerIdentityTelemetrySignal signal);
}

internal static class CustomerIdentityBoundaryValidation
{
    internal const int CustomerIdMaximumLength = 128;
    internal const int RequestIdMaximumLength = 256;
    internal const int CorrelationIdMaximumLength = 256;

    private static readonly HashSet<string> AcceptedActorTypes = new(StringComparer.Ordinal)
    {
        "CUSTOMER",
        "ADVISOR",
        "ADMINISTRATOR",
        "PROFESSIONAL",
    };

    private static readonly HashSet<string> AcceptedTelemetryOperations = new(StringComparer.Ordinal)
    {
        D13ContractNames.GetCustomer,
        D13ContractNames.GetTrustedActorContext,
        D13ContractNames.RegisterOrUpdateCustomer,
        D13ContractNames.RecordKycResult,
    };

    private static readonly HashSet<string> AcceptedTelemetryOutcomes =
        Enum.GetNames<ContractOutcome>().ToHashSet(StringComparer.Ordinal);

    internal static bool IsSafeIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    internal static bool IsSafeTelemetryEnvelope(
        string? customerId,
        string? requestId,
        string? correlationId) =>
        IsSafeIdentifier(customerId, CustomerIdMaximumLength) &&
        IsSafeIdentifier(requestId, RequestIdMaximumLength) &&
        IsSafeIdentifier(correlationId, CorrelationIdMaximumLength);

    internal static bool IsSafeTelemetrySignal(CustomerIdentityTelemetrySignal? signal) =>
        signal is not null &&
        AcceptedTelemetryOperations.Contains(signal.Operation) &&
        AcceptedTelemetryOutcomes.Contains(signal.Outcome) &&
        IsSafeTelemetryEnvelope(signal.CustomerId, signal.RequestId, signal.CorrelationId);

    internal static bool IsValidCanonicalActor(ActorContext? actor) =>
        actor is not null &&
        IsSafeIdentifier(actor.ActorId, 128) &&
        IsSafeIdentifier(actor.AuthenticationContextId, 256) &&
        actor.AuthenticatedAt != default &&
        AcceptedActorTypes.Contains(actor.ActorType);

    internal static bool IsValidCanonicalResolution(AuthenticationResolution resolution) =>
        resolution.FailureCategory is null &&
        resolution.FailureCode is null &&
        !resolution.Retryable &&
        IsSafeIdentifier(resolution.CustomerId, CustomerIdMaximumLength) &&
        IsValidCanonicalActor(resolution.Actor);
}
