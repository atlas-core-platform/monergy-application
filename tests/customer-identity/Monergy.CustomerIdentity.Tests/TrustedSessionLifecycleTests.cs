using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class TrustedSessionLifecycleTests
{
    [Fact]
    public async Task SuccessfulAuthenticationEstablishesCurrentTimeBoundedSessionWithoutAuthorizationOrConsent()
    {
        var harness = new D13Harness();

        var result = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        var session = Assert.Single(harness.Sessions.Snapshot());
        Assert.Equal(D13Harness.Customer, session.CustomerId);
        Assert.Equal(result.Data, session.Actor);
        Assert.Equal(harness.Clock.Now, session.CreatedAt);
        Assert.Equal(harness.Clock.Now.AddMinutes(30), session.ExpiresAt);
        Assert.False(session.Revoked);
        Assert.Null(session.RevokedAt);
        Assert.Equal(1, session.Revision);
        Assert.Equal(1, harness.Sessions.CreationEffectCount);
        Assert.DoesNotContain("authorization", JsonSerializer.Serialize(result.Data, ContractJson.Options),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consent", JsonSerializer.Serialize(result.Data, ContractJson.Options),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExactExpiryBoundaryFailsClosedWithoutFallingBackToProviderResult()
    {
        var harness = new D13Harness();
        var first = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        var expiresAt = Assert.Single(harness.Sessions.Snapshot()).ExpiresAt;

        harness.Clock.Now = expiresAt.AddTicks(-1);
        var before = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        harness.Clock.Now = expiresAt;
        var atBoundary = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        harness.Clock.Now = expiresAt.AddMinutes(1);
        var after = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(ContractOutcome.Success, before.Outcome);
        Assert.All(new[] { atBoundary, after }, result =>
        {
            Assert.Equal(ContractOutcome.Rejected, result.Outcome);
            Assert.Equal("identity.session.not-current", result.Error?.Code);
            Assert.Equal(ContractErrorCategory.AuthenticationRequired, result.Error?.Category);
            Assert.Null(result.Data);
        });
        Assert.Equal(1, harness.Sessions.CreationEffectCount);
        Assert.Single(harness.Events.Snapshot());
    }

    [Fact]
    public async Task RevocationIsIrreversibleIdempotentAndNewAuthenticationUsesNewContext()
    {
        var harness = new D13Harness();
        var active = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        var revoked = harness.SessionLifecycle.Revoke(active.Data!.AuthenticationContextId,
            D13Harness.Customer, active.Data.ActorId, "revoke-request", "revoke-correlation");
        var duplicate = harness.SessionLifecycle.Revoke(active.Data.AuthenticationContextId,
            D13Harness.Customer, active.Data.ActorId, "revoke-request", "revoke-correlation");
        var stale = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal(TrustedSessionRevocationStatus.Revoked, revoked.Status);
        Assert.Equal(TrustedSessionRevocationStatus.AlreadyRevoked, duplicate.Status);
        Assert.Equal(ContractOutcome.Rejected, stale.Outcome);
        Assert.Equal(1, harness.Sessions.RevocationEffectCount);
        Assert.Equal(2, harness.Events.Snapshot().Length);

        harness.Provider.Register(new("reference-auth-customer-a-new", D13Harness.Customer,
            "actor-customer-a", "CUSTOMER", harness.Clock.Now,
            "reference-authentication-context-a-new"));
        var renewed = await harness.App.GetTrustedActorContextAsync(
            harness.GetActor("reference-auth-customer-a-new"));

        Assert.Equal(ContractOutcome.Success, renewed.Outcome);
        Assert.Equal("reference-authentication-context-a-new", renewed.Data?.AuthenticationContextId);
        Assert.True(Assert.Single(harness.Sessions.Snapshot(), item =>
            item.Actor.AuthenticationContextId == active.Data.AuthenticationContextId).Revoked);
    }

    [Fact]
    public async Task CustomerActorUnknownAndMalformedContextPathsFailClosedWithoutExistenceDetail()
    {
        var harness = new D13Harness();
        await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        harness.Provider.Register(new(D13Harness.AuthenticationReference, D13Harness.Customer,
            "different-actor", "CUSTOMER", DateTimeOffset.UnixEpoch,
            "reference-authentication-context-a"));

        var actorMismatch = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        var unknown = await harness.App.GetTrustedActorContextAsync(harness.GetActor("unknown-reference"));
        var malformed = await harness.App.GetTrustedActorContextAsync(harness.GetActor(" "));
        var crossCustomer = await harness.App.GetTrustedActorContextAsync(
            harness.GetActor(customerId: "customer-b"));
        var unknownRevocation = harness.SessionLifecycle.Revoke("unknown-context", D13Harness.Customer,
            "different-actor", "revoke-unknown", "correlation-unknown");

        Assert.Equal(ContractErrorCategory.AuthenticationRequired, actorMismatch.Error?.Category);
        Assert.Equal("identity.session.not-current", actorMismatch.Error?.Code);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired, unknown.Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired, malformed.Error?.Category);
        Assert.Equal(ContractErrorCategory.AccessDenied, crossCustomer.Error?.Category);
        Assert.Equal(TrustedSessionRevocationStatus.NotCurrent, unknownRevocation.Status);
        Assert.All(new[] { actorMismatch, unknown, malformed, crossCustomer }, result => Assert.Null(result.Data));
    }

    [Fact]
    public async Task ConcurrentCreationAndRevocationCannotResurrectSessionAndSnapshotsAreDetached()
    {
        var harness = new D13Harness();
        var requests = Enumerable.Range(0, 16)
            .Select(index => harness.GetActor() with
            {
                RequestId = $"request-create-{index}",
                CorrelationId = $"correlation-create-{index}",
            });
        var created = await Task.WhenAll(requests.Select(request =>
            harness.App.GetTrustedActorContextAsync(request)));

        Assert.All(created, result => Assert.Equal(ContractOutcome.Success, result.Outcome));
        Assert.Equal(1, harness.Sessions.CreationEffectCount);
        Assert.Single(harness.Events.Snapshot());

        var session = Assert.Single(harness.Sessions.Snapshot());
        var resolutionTasks = Enumerable.Range(0, 16).Select(index =>
            harness.App.GetTrustedActorContextAsync(harness.GetActor() with
            {
                RequestId = $"request-race-{index}",
                CorrelationId = $"correlation-race-{index}",
            })).Cast<Task>().ToList();
        resolutionTasks.Add(Task.Run(() => harness.SessionLifecycle.Revoke(
            session.Actor.AuthenticationContextId, session.CustomerId, session.Actor.ActorId,
            "request-race-revoke", "correlation-race-revoke")));
        await Task.WhenAll(resolutionTasks);

        var final = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        Assert.Equal(ContractOutcome.Rejected, final.Outcome);
        Assert.True(Assert.Single(harness.Sessions.Snapshot()).Revoked);
        Assert.Equal(1, harness.Sessions.RevocationEffectCount);

        var callerCopy = session with
        {
            Actor = session.Actor with { ActorId = "caller-mutated" },
            Revoked = false,
        };
        Assert.Equal("caller-mutated", callerCopy.Actor.ActorId);
        var retained = Assert.Single(harness.Sessions.Snapshot());
        Assert.Equal("actor-customer-a", retained.Actor.ActorId);
        Assert.True(retained.Revoked);
    }

    [Fact]
    public async Task Cid005ProducerEmitsMinimalImmutableReplaySafeAuditCompatibleFacts()
    {
        var harness = new D13Harness();
        var current = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        harness.SessionLifecycle.Revoke(current.Data!.AuthenticationContextId, D13Harness.Customer,
            current.Data.ActorId, "request-revoke", "correlation-revoke");
        harness.SessionLifecycle.Revoke(current.Data.AuthenticationContextId, D13Harness.Customer,
            current.Data.ActorId, "request-revoke", "correlation-revoke");

        var events = harness.Events.Snapshot();
        Assert.Equal(2, events.Length);
        Assert.Equal([1, 2], events.Select(message => message.Payload.Revision).ToArray());
        Assert.Equal(["REFERENCE_SESSION_ESTABLISHED", "REFERENCE_SESSION_REVOKED"],
            events.Select(message => message.Payload.ChangeKind).ToArray());
        Assert.All(events, message =>
        {
            Assert.Equal("CID-005", message.ContractId);
            Assert.Equal(D14ContractNames.CustomerIdentityChanged, message.EventName);
            Assert.Equal("Customer & Identity Service", message.Producer);
            Assert.Equal("Customer", message.SubjectType);
            Assert.Equal(D13Harness.Customer, message.SubjectId);
            var audit = new AuditableEvent(message.ContractId, message.EventId, message.EventName,
                message.EventVersion, message.OccurredAt, message.CorrelationId, message.CausationId,
                message.Producer, message.SubjectType, message.SubjectId);
            Assert.Equal(message.ContractId, audit.ContractId);
            Assert.Equal(message.EventId, audit.EventId);
        });

        var wire = JsonSerializer.Serialize(events, ContractJson.Options);
        Assert.DoesNotContain(D13Harness.AuthenticationReference, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Data.AuthenticationContextId, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Data.ActorId, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", wire, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, harness.Events.Snapshot().Length);
    }

    [Fact]
    public async Task SessionDependencyFailureAndUnverifiableTimeFailClosedWithSafeTelemetry()
    {
        var dependencyHarness = new D13Harness();
        dependencyHarness.Sessions.FailNextAccess();
        var dependency = await dependencyHarness.App.GetTrustedActorContextAsync(
            dependencyHarness.GetActor());

        Assert.Equal(ContractOutcome.Failed, dependency.Outcome);
        Assert.Equal(ContractErrorCategory.DependencyFailure, dependency.Error?.Category);
        Assert.Null(dependency.Data);
        Assert.Empty(dependencyHarness.Sessions.Snapshot());

        var futureHarness = new D13Harness();
        futureHarness.Provider.Register(new("future-reference", D13Harness.Customer,
            "actor-customer-a", "CUSTOMER", futureHarness.Clock.Now.AddMinutes(1), "future-context"));
        var unverifiable = await futureHarness.App.GetTrustedActorContextAsync(
            futureHarness.GetActor("future-reference"));

        Assert.Equal(ContractOutcome.Rejected, unverifiable.Outcome);
        Assert.Equal("identity.session.not-current", unverifiable.Error?.Code);
        Assert.Empty(futureHarness.Sessions.Snapshot());
        var telemetryWire = JsonSerializer.Serialize(
            dependencyHarness.Telemetry.Snapshot().Concat(futureHarness.Telemetry.Snapshot()),
            ContractJson.Options);
        Assert.DoesNotContain(D13Harness.AuthenticationReference, telemetryWire, StringComparison.Ordinal);
        Assert.DoesNotContain("future-context", telemetryWire, StringComparison.Ordinal);
        Assert.Contains("correlation-actor", telemetryWire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeLifecycleMetadataAndMalformedFactsCannotEnterCid005Sink()
    {
        var harness = new D13Harness();
        var current = await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        var unsafeRevocation = harness.SessionLifecycle.Revoke(current.Data!.AuthenticationContextId,
            D13Harness.Customer, current.Data.ActorId, "request-revoke", "correlation\nunsafe");

        Assert.Equal(TrustedSessionRevocationStatus.NotCurrent, unsafeRevocation.Status);
        Assert.False(Assert.Single(harness.Sessions.Snapshot()).Revoked);
        Assert.Single(harness.Events.Snapshot());

        harness.Events.Publish(new("CID-005", "unsafe-event", D14ContractNames.CustomerIdentityChanged,
            ContractGuard.CurrentVersion, harness.Clock.Now, "correlation\nunsafe", null,
            "Customer & Identity Service", "Customer", D13Harness.Customer,
            new("REFERENCE_SESSION_REVOKED", 2)));
        harness.Events.Publish(new("CID-005", "unsafe-event-2", D14ContractNames.CustomerIdentityChanged,
            ContractGuard.CurrentVersion, harness.Clock.Now, "correlation", null,
            "Customer & Identity Service", "Customer", D13Harness.Customer,
            new("PROVIDER_NATIVE_CHANGE", 2)));

        Assert.Single(harness.Events.Snapshot());
    }
}
