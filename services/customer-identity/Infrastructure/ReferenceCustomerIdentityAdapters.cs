using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed record ReferenceAuthenticationRecord(
    string AuthenticationReference,
    string CustomerId,
    string ActorId,
    string ActorType,
    DateTimeOffset AuthenticatedAt,
    string AuthenticationContextId);

public sealed class ReferenceCustomerAuthenticationProvider : ICustomerAuthenticationProvider
{
    private readonly object sync = new();
    private readonly Dictionary<string, ReferenceAuthenticationRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthenticationResolution> failures = new(StringComparer.Ordinal);

    public ReferenceCustomerAuthenticationProvider(IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        Register(new("reference-auth-customer-a", "customer-a", "actor-customer-a", "CUSTOMER",
            DateTimeOffset.UnixEpoch, "reference-authentication-context-a"));
        Register(new("reference-auth-customer-b", "customer-b", "actor-customer-b", "CUSTOMER",
            DateTimeOffset.UnixEpoch, "reference-authentication-context-b"));
    }

    public void Register(ReferenceAuthenticationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var actor = new ActorContext(record.ActorId, record.ActorType, record.AuthenticatedAt,
            record.AuthenticationContextId);
        if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(record.AuthenticationReference, 256) ||
            !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                record.CustomerId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength) ||
            !CustomerIdentityBoundaryValidation.IsValidCanonicalActor(actor))
        {
            throw new ArgumentException("The reference authentication record is malformed.", nameof(record));
        }

        lock (sync)
        {
            records[record.AuthenticationReference] = record;
            failures.Remove(record.AuthenticationReference);
        }
    }

    public void SetFailure(
        string authenticationReference,
        ContractErrorCategory category = ContractErrorCategory.DependencyFailure,
        bool retryable = true)
    {
        lock (sync)
        {
            failures[authenticationReference] = AuthenticationResolution.Failed(
                "identity.authentication.provider-failed", category, retryable);
        }
    }

    public Task<AuthenticationResolution> ResolveAsync(
        AuthenticationReference authentication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (failures.TryGetValue(authentication.Value, out var failure))
            {
                return Task.FromResult(failure);
            }

            if (!records.TryGetValue(authentication.Value, out var record) ||
                !string.Equals(record.CustomerId, authentication.CustomerId, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticationResolution.Failed(
                    "identity.authentication.unverifiable", ContractErrorCategory.AuthenticationRequired));
            }

            var actor = new ActorContext(record.ActorId, record.ActorType, record.AuthenticatedAt,
                record.AuthenticationContextId);
            if (!CustomerIdentityBoundaryValidation.IsValidCanonicalActor(actor) ||
                !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                    record.CustomerId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength))
            {
                return Task.FromResult(AuthenticationResolution.Failed(
                    "identity.authentication.unverifiable", ContractErrorCategory.AuthenticationRequired));
            }

            return Task.FromResult(AuthenticationResolution.Succeeded(actor, record.CustomerId));
        }
    }
}

public sealed class InMemoryTrustedSessionRepository : ITrustedSessionRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, TrustedSessionSnapshot> sessions = new(StringComparer.Ordinal);
    private bool failNextAccess;

    public InMemoryTrustedSessionRepository(IConfiguration configuration) =>
        ReferenceAdapterGuard.EnsureAllowed(configuration);

    public int CreationEffectCount { get; private set; }
    public int RevocationEffectCount { get; private set; }

    public void FailNextAccess()
    {
        lock (sync) { failNextAccess = true; }
    }

    public TrustedSessionEvaluation EstablishOrEvaluate(
        string customerId,
        ActorContext actor,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        lock (sync)
        {
            if (ConsumeFailure())
            {
                return new(TrustedSessionEvaluationStatus.DependencyFailure, null, false);
            }

            if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                    customerId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength) ||
                !CustomerIdentityBoundaryValidation.IsValidCanonicalActor(actor) ||
                actor.AuthenticatedAt > now || lifetime <= TimeSpan.Zero)
            {
                return new(TrustedSessionEvaluationStatus.Unverifiable, null, false);
            }

            if (sessions.TryGetValue(actor.AuthenticationContextId, out var current))
            {
                if (!string.Equals(current.CustomerId, customerId, StringComparison.Ordinal) ||
                    current.Actor != actor)
                {
                    return new(TrustedSessionEvaluationStatus.Mismatch, Copy(current), false);
                }

                if (current.Revoked)
                {
                    return new(TrustedSessionEvaluationStatus.Revoked, Copy(current), false);
                }

                if (now < current.CreatedAt)
                {
                    return new(TrustedSessionEvaluationStatus.Unverifiable, Copy(current), false);
                }

                return now >= current.ExpiresAt
                    ? new(TrustedSessionEvaluationStatus.Expired, Copy(current), false)
                    : new(TrustedSessionEvaluationStatus.Current, Copy(current), false);
            }

            DateTimeOffset expiresAt;
            try
            {
                expiresAt = now.Add(lifetime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new(TrustedSessionEvaluationStatus.Unverifiable, null, false);
            }

            var created = new TrustedSessionSnapshot(customerId, actor with { }, now, expiresAt,
                false, null, 1);
            sessions.Add(actor.AuthenticationContextId, created);
            CreationEffectCount++;
            return new(TrustedSessionEvaluationStatus.Current, Copy(created), true);
        }
    }

    public TrustedSessionRevocation Revoke(
        string authenticationContextId,
        string customerId,
        string actorId,
        DateTimeOffset now)
    {
        lock (sync)
        {
            if (ConsumeFailure())
            {
                return new(TrustedSessionRevocationStatus.DependencyFailure, null);
            }

            if (!CustomerIdentityBoundaryValidation.IsSafeIdentifier(authenticationContextId, 256) ||
                !CustomerIdentityBoundaryValidation.IsSafeIdentifier(
                    customerId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength) ||
                !CustomerIdentityBoundaryValidation.IsSafeIdentifier(actorId, 128) ||
                !sessions.TryGetValue(authenticationContextId, out var current) ||
                !string.Equals(current.CustomerId, customerId, StringComparison.Ordinal) ||
                !string.Equals(current.Actor.ActorId, actorId, StringComparison.Ordinal))
            {
                return new(TrustedSessionRevocationStatus.NotCurrent, null);
            }

            if (current.Revoked)
            {
                return new(TrustedSessionRevocationStatus.AlreadyRevoked, Copy(current));
            }

            if (now < current.CreatedAt || now >= current.ExpiresAt)
            {
                return new(TrustedSessionRevocationStatus.NotCurrent, null);
            }

            var revoked = current with { Revoked = true, RevokedAt = now, Revision = current.Revision + 1 };
            sessions[authenticationContextId] = revoked;
            RevocationEffectCount++;
            return new(TrustedSessionRevocationStatus.Revoked, Copy(revoked));
        }
    }

    public ImmutableArray<TrustedSessionSnapshot> Snapshot()
    {
        lock (sync)
        {
            return sessions.Values
                .OrderBy(session => session.Actor.AuthenticationContextId, StringComparer.Ordinal)
                .Select(Copy)
                .ToImmutableArray();
        }
    }

    private bool ConsumeFailure()
    {
        var failed = failNextAccess;
        failNextAccess = false;
        return failed;
    }

    private static TrustedSessionSnapshot Copy(TrustedSessionSnapshot session) =>
        session with { Actor = session.Actor with { } };
}

public sealed class InMemoryCustomerIdentityEventSink : ICustomerIdentityEventSink
{
    private static readonly HashSet<string> AcceptedChangeKinds = new(StringComparer.Ordinal)
    {
        "REFERENCE_SESSION_ESTABLISHED",
        "REFERENCE_SESSION_REVOKED",
    };

    private readonly object sync = new();
    private readonly Dictionary<string, DomainEvent<CustomerIdentityChangedPayload>> messages =
        new(StringComparer.Ordinal);

    public InMemoryCustomerIdentityEventSink(IConfiguration configuration) =>
        ReferenceAdapterGuard.EnsureAllowed(configuration);

    public void Publish(DomainEvent<CustomerIdentityChangedPayload> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsSafe(message))
        {
            return;
        }

        lock (sync)
        {
            messages.TryAdd(message.EventId, Copy(message));
        }
    }

    public ImmutableArray<DomainEvent<CustomerIdentityChangedPayload>> Snapshot()
    {
        lock (sync)
        {
            return messages.Values
                .OrderBy(message => message.OccurredAt)
                .ThenBy(message => message.Payload.Revision)
                .Select(Copy)
                .ToImmutableArray();
        }
    }

    private static DomainEvent<CustomerIdentityChangedPayload> Copy(
        DomainEvent<CustomerIdentityChangedPayload> message) =>
        message with { Payload = message.Payload with { } };

    private static bool IsSafe(DomainEvent<CustomerIdentityChangedPayload> message) =>
        string.Equals(message.ContractId, "CID-005", StringComparison.Ordinal) &&
        string.Equals(message.EventName, D14ContractNames.CustomerIdentityChanged, StringComparison.Ordinal) &&
        string.Equals(message.EventVersion, ContractGuard.CurrentVersion, StringComparison.Ordinal) &&
        string.Equals(message.Producer, "Customer & Identity Service", StringComparison.Ordinal) &&
        string.Equals(message.SubjectType, "Customer", StringComparison.Ordinal) &&
        message.OccurredAt != default &&
        CustomerIdentityBoundaryValidation.IsSafeIdentifier(message.EventId, 128) &&
        CustomerIdentityBoundaryValidation.IsSafeIdentifier(
            message.CorrelationId, CustomerIdentityBoundaryValidation.CorrelationIdMaximumLength) &&
        (message.CausationId is null || CustomerIdentityBoundaryValidation.IsSafeIdentifier(
            message.CausationId, CustomerIdentityBoundaryValidation.RequestIdMaximumLength)) &&
        CustomerIdentityBoundaryValidation.IsSafeIdentifier(
            message.SubjectId, CustomerIdentityBoundaryValidation.CustomerIdMaximumLength) &&
        message.Payload is not null && message.Payload.Revision > 0 &&
        AcceptedChangeKinds.Contains(message.Payload.ChangeKind);
}

public sealed class InMemoryCustomerIdentityRepository : ICustomerIdentityRepository
{
    private sealed record StoredCustomerMutation(string Fingerprint, CustomerProjection Result);
    private sealed record StoredKycMutation(string Fingerprint, KycVerification Result);

    private readonly object sync = new();
    private readonly Dictionary<string, CustomerProjection> customers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<KycVerification>> kycHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<CustomerMutationIdentity, StoredCustomerMutation> customerMutations = [];
    private readonly Dictionary<KycMutationIdentity, StoredKycMutation> kycMutations = [];
    private bool failNextCustomerRead;
    private bool failNextCustomerWrite;
    private bool failNextKycWrite;

    public InMemoryCustomerIdentityRepository(IConfiguration configuration) =>
        ReferenceAdapterGuard.EnsureAllowed(configuration);

    public int CustomerAuthoritativeEffectCount { get; private set; }
    public int KycAuthoritativeEffectCount { get; private set; }

    public void FailNextCustomerRead()
    {
        lock (sync) { failNextCustomerRead = true; }
    }

    public void FailNextCustomerWrite()
    {
        lock (sync) { failNextCustomerWrite = true; }
    }

    public void FailNextKycWrite()
    {
        lock (sync) { failNextKycWrite = true; }
    }

    public RepositoryRead<CustomerProjection> GetCustomer(string customerId)
    {
        lock (sync)
        {
            if (failNextCustomerRead)
            {
                failNextCustomerRead = false;
                return new(null, RepositoryFailure.DependencyFailure);
            }

            return customers.TryGetValue(customerId, out var customer)
                ? new(customer with { }, RepositoryFailure.None)
                : new(null, RepositoryFailure.NotFound);
        }
    }

    public CustomerMutation RegisterOrUpdateCustomer(
        CustomerMutationIdentity identity,
        string payloadFingerprint,
        RegisterOrUpdateCustomer payload,
        DateTimeOffset recordedAt)
    {
        lock (sync)
        {
            if (customerMutations.TryGetValue(identity, out var committed))
            {
                return string.Equals(committed.Fingerprint, payloadFingerprint, StringComparison.Ordinal)
                    ? new(committed.Result with { }, MutationDisposition.Replayed, RepositoryFailure.None)
                    : new(null, MutationDisposition.Replayed, RepositoryFailure.DuplicateRequest);
            }

            if (failNextCustomerWrite)
            {
                failNextCustomerWrite = false;
                return new(null, MutationDisposition.Replayed, RepositoryFailure.DependencyFailure);
            }

            var exists = customers.TryGetValue(payload.CustomerId, out var current);
            if ((!exists && payload.ExpectedRevision is not null) ||
                (exists && (payload.ExpectedRevision is null || payload.ExpectedRevision != current!.Revision)))
            {
                return new(null, MutationDisposition.Replayed, RepositoryFailure.Conflict);
            }

            var disposition = exists ? MutationDisposition.Updated : MutationDisposition.Created;
            var result = new CustomerProjection(payload.CustomerId, payload.DisplayName,
                exists ? current!.Revision + 1 : 1, recordedAt);
            customers[payload.CustomerId] = result;
            customerMutations[identity] = new(payloadFingerprint, result);
            CustomerAuthoritativeEffectCount++;
            return new(result with { }, disposition, RepositoryFailure.None);
        }
    }

    public KycMutation RecordKycResult(
        KycMutationIdentity identity,
        string payloadFingerprint,
        RecordKycResult payload,
        DateTimeOffset recordedAt)
    {
        lock (sync)
        {
            if (kycMutations.TryGetValue(identity, out var committed))
            {
                return string.Equals(committed.Fingerprint, payloadFingerprint, StringComparison.Ordinal)
                    ? new(committed.Result with { }, MutationDisposition.Replayed, RepositoryFailure.None)
                    : new(null, MutationDisposition.Replayed, RepositoryFailure.DuplicateRequest);
            }

            if (failNextKycWrite)
            {
                failNextKycWrite = false;
                return new(null, MutationDisposition.Replayed, RepositoryFailure.DependencyFailure);
            }

            if (!customers.ContainsKey(payload.CustomerId))
            {
                return new(null, MutationDisposition.Replayed, RepositoryFailure.NotFound);
            }

            if (!kycHistory.TryGetValue(payload.CustomerId, out var history))
            {
                history = [];
                kycHistory[payload.CustomerId] = history;
            }

            var result = new KycVerification(payload.CustomerId, payload.VerificationId,
                payload.StatusCode, payload.DeterminedAt, history.Count + 1, recordedAt);
            history.Add(result);
            kycMutations[identity] = new(payloadFingerprint, result);
            KycAuthoritativeEffectCount++;
            return new(result with { }, MutationDisposition.Created, RepositoryFailure.None);
        }
    }

    public IReadOnlyList<KycVerification> GetKycHistory(string customerId)
    {
        lock (sync)
        {
            return kycHistory.TryGetValue(customerId, out var history)
                ? history.Select(item => item with { }).ToImmutableArray()
                : [];
        }
    }
}

public sealed class InMemoryCustomerIdentityTelemetry : ICustomerIdentityTelemetry
{
    private readonly object sync = new();
    private readonly List<CustomerIdentityTelemetrySignal> signals = [];

    public InMemoryCustomerIdentityTelemetry(IConfiguration configuration) =>
        ReferenceAdapterGuard.EnsureAllowed(configuration);

    public void Record(CustomerIdentityTelemetrySignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!CustomerIdentityBoundaryValidation.IsSafeTelemetrySignal(signal))
        {
            return;
        }

        lock (sync) { signals.Add(signal with { }); }
    }

    public ImmutableArray<CustomerIdentityTelemetrySignal> Snapshot()
    {
        lock (sync) { return signals.Select(signal => signal with { }).ToImmutableArray(); }
    }
}
