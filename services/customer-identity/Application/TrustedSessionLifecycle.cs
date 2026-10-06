using System.Security.Cryptography;
using System.Text.Json;
using Monergy.Contracts;

namespace Monergy.Services.CustomerIdentity.Application;

public sealed class TrustedSessionLifecycle(
    ITrustedSessionRepository repository,
    ICustomerIdentityEventSink eventSink,
    TrustedSessionPolicy policy,
    TimeProvider clock)
{
    private const string Producer = "Customer & Identity Service";
    private const string EstablishedChange = "REFERENCE_SESSION_ESTABLISHED";
    private const string RevokedChange = "REFERENCE_SESSION_REVOKED";
    private readonly object sync = new();

    public TrustedSessionEvaluation EstablishOrEvaluate(
        string customerId,
        ActorContext actor,
        string requestId,
        string correlationId)
    {
        if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                requestId, CustomerIdentityBoundaryValidation.RequestIdMaximumLength) ||
            !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                correlationId, CustomerIdentityBoundaryValidation.CorrelationIdMaximumLength))
        {
            return new(TrustedSessionEvaluationStatus.Unverifiable, null, false);
        }

        lock (sync)
        {
            var evaluation = repository.EstablishOrEvaluate(customerId, actor, clock.GetUtcNow(), policy.Lifetime);
            if (evaluation.Created && evaluation.Session is not null)
            {
                eventSink.Publish(CreateEvent(evaluation.Session, EstablishedChange, requestId, correlationId));
            }

            return evaluation;
        }
    }

    // This is an owner-local reference control, not a public Monergy contract.
    public TrustedSessionRevocation Revoke(
        string authenticationContextId,
        string customerId,
        string actorId,
        string requestId,
        string correlationId)
    {
        if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                requestId, CustomerIdentityBoundaryValidation.RequestIdMaximumLength) ||
            !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                correlationId, CustomerIdentityBoundaryValidation.CorrelationIdMaximumLength))
        {
            return new(TrustedSessionRevocationStatus.NotCurrent, null);
        }

        lock (sync)
        {
            var result = repository.Revoke(authenticationContextId, customerId, actorId, clock.GetUtcNow());
            if (result.Status == TrustedSessionRevocationStatus.Revoked && result.Session is not null)
            {
                eventSink.Publish(CreateEvent(result.Session, RevokedChange, requestId, correlationId));
            }

            return result;
        }
    }

    private static DomainEvent<CustomerIdentityChangedPayload> CreateEvent(
        TrustedSessionSnapshot session,
        string changeKind,
        string requestId,
        string correlationId)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            session.CustomerId,
            session.Actor.AuthenticationContextId,
            ChangeKind = changeKind,
            session.Revision,
        }, ContractJson.Options);
        var eventId = $"customer-identity-change-{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}";
        var occurredAt = session.RevokedAt ?? session.CreatedAt;
        return new("CID-005", eventId, D14ContractNames.CustomerIdentityChanged,
            ContractGuard.CurrentVersion, occurredAt, correlationId, requestId, Producer,
            "Customer", session.CustomerId, new(changeKind, session.Revision));
    }
}
