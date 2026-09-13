using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Monergy.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ContractOutcome>))]
public enum ContractOutcome
{
    Success,
    Rejected,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContractErrorCategory>))]
public enum ContractErrorCategory
{
    ValidationError,
    AuthenticationRequired,
    AccessDenied,
    ConsentRequired,
    ConsentExpired,
    ConsentRevoked,
    NotFound,
    Conflict,
    PreconditionFailed,
    DuplicateRequest,
    RateLimited,
    TemporarilyUnavailable,
    DependencyFailure,
    ProcessingFailed,
    UnsupportedOperation,
}

public sealed record ActorContext(
    string ActorId,
    string ActorType,
    DateTimeOffset AuthenticatedAt,
    string AuthenticationContextId);

public sealed record WorkloadContext(string WorkloadId, string WorkloadIdentityId);

public sealed record AccessContext(
    string Purpose,
    string? ConsentReferenceId,
    string AuthorizationContextId,
    string CustomerId);

public sealed record TrustedSecurityContext(
    ActorContext Actor,
    WorkloadContext Workload,
    AccessContext Access);

public sealed record ContractRequest<TPayload>(
    string ContractName,
    string ContractVersion,
    string RequestId,
    string CorrelationId,
    string? CausationId,
    TrustedSecurityContext Security,
    string? IdempotencyKey,
    TPayload Payload);

public sealed record ContractError(
    string Code,
    ContractErrorCategory Category,
    string Message,
    bool Retryable,
    string CorrelationId);

public sealed record ContractResult<TData>(
    string ContractName,
    string ContractVersion,
    string RequestId,
    string CorrelationId,
    ContractOutcome Outcome,
    TData? Data,
    ContractError? Error)
{
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory preserves the strongly typed contract-result invariant.")]
    public static ContractResult<TData> Succeeded(ContractRequest<object?> context, TData data) =>
        new(context.ContractName, context.ContractVersion, context.RequestId, context.CorrelationId, ContractOutcome.Success, data, null);

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory preserves the strongly typed contract-result invariant.")]
    public static ContractResult<TData> Succeeded<TPayload>(ContractRequest<TPayload> context, TData data) =>
        new(context.ContractName, context.ContractVersion, context.RequestId, context.CorrelationId, ContractOutcome.Success, data, null);

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory preserves the strongly typed contract-result invariant.")]
    public static ContractResult<TData> Rejected<TPayload>(
        ContractRequest<TPayload> context,
        string code,
        ContractErrorCategory category,
        string message,
        bool retryable = false) =>
        new(
            context.ContractName,
            context.ContractVersion,
            context.RequestId,
            context.CorrelationId,
            ContractOutcome.Rejected,
            default,
            new ContractError(code, category, message, retryable, context.CorrelationId));

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory preserves the strongly typed contract-result invariant.")]
    public static ContractResult<TData> Failed<TPayload>(
        ContractRequest<TPayload> context,
        string code,
        ContractErrorCategory category,
        string message,
        bool retryable) =>
        new(
            context.ContractName,
            context.ContractVersion,
            context.RequestId,
            context.CorrelationId,
            ContractOutcome.Failed,
            default,
            new ContractError(code, category, message, retryable, context.CorrelationId));
}

public sealed record DomainEvent<TPayload>(
    string ContractId,
    string EventId,
    string EventName,
    string EventVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? CausationId,
    string Producer,
    string SubjectType,
    string SubjectId,
    TPayload Payload);

public sealed record AuditableEvent(
    string ContractId,
    string EventId,
    string EventName,
    string EventVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? CausationId,
    string Producer,
    string SubjectType,
    string SubjectId);

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };
}

public static class ContractGuard
{
    public const string CurrentVersion = "1.0.0";

    public static ContractError? Validate<TPayload>(ContractRequest<TPayload> request, string expectedName)
    {
        if (!string.Equals(request.ContractName, expectedName, StringComparison.Ordinal) ||
            !string.Equals(request.ContractVersion, CurrentVersion, StringComparison.Ordinal))
        {
            return Error("contract.unsupported", ContractErrorCategory.UnsupportedOperation, "The contract name or version is unsupported.", request.CorrelationId);
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return Error("contract.context.invalid", ContractErrorCategory.ValidationError, "Request and correlation identifiers are required.", request.CorrelationId);
        }

        var security = request.Security;
        if (security.Actor is null || security.Workload is null || security.Access is null ||
            string.IsNullOrWhiteSpace(security.Actor.ActorId) ||
            string.IsNullOrWhiteSpace(security.Actor.AuthenticationContextId) ||
            string.IsNullOrWhiteSpace(security.Workload.WorkloadIdentityId))
        {
            return Error("security.authentication.required", ContractErrorCategory.AuthenticationRequired, "Trusted actor and workload context are required.", request.CorrelationId);
        }

        if (string.IsNullOrWhiteSpace(security.Access.AuthorizationContextId) ||
            string.IsNullOrWhiteSpace(security.Access.Purpose) ||
            string.IsNullOrWhiteSpace(security.Access.CustomerId))
        {
            return Error("security.access.denied", ContractErrorCategory.AccessDenied, "Purpose, customer and authorization context are required.", request.CorrelationId);
        }

        return null;
    }

    private static ContractError Error(
        string code,
        ContractErrorCategory category,
        string message,
        string correlationId) =>
        new(code, category, message, false, correlationId ?? string.Empty);
}
