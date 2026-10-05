using System.Text.Json;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class AuthenticationBoundaryTests
{
    [Fact]
    public async Task ReferenceProviderResolvesCanonicalActorWithoutLeakingProviderSchema()
    {
        var harness = new D13Harness();
        var result = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        Assert.Equal("actor-customer-a", result.Data?.ActorId);
        Assert.Equal("CUSTOMER", result.Data?.ActorType);
        Assert.Equal("reference-authentication-context-a", result.Data?.AuthenticationContextId);
        var wire = JsonSerializer.Serialize(result, ContractJson.Options);
        Assert.DoesNotContain(D13Harness.AuthenticationReference, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownMalformedAndCustomerMismatchedAuthenticationReferencesFailClosed()
    {
        var harness = new D13Harness();
        var unknown = await harness.App.GetTrustedActorContextAsync(harness.GetActor("unknown-reference"));
        var malformed = await harness.App.GetTrustedActorContextAsync(harness.GetActor(" "));
        var mismatch = await harness.App.GetTrustedActorContextAsync(
            harness.GetActor(customerId: "customer-b"));

        Assert.Equal(ContractErrorCategory.AuthenticationRequired, unknown.Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired, malformed.Error?.Category);
        Assert.Equal(ContractErrorCategory.AccessDenied, mismatch.Error?.Category);
        Assert.All(new[] { unknown, malformed, mismatch }, item => Assert.Null(item.Data));
    }

    [Fact]
    public async Task ProviderFailureMapsToExistingMonergyCategoryWithoutNativeDetail()
    {
        var harness = new D13Harness();
        harness.Provider.SetFailure(D13Harness.AuthenticationReference,
            ContractErrorCategory.DependencyFailure, retryable: true);

        var result = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal(ContractOutcome.Failed, result.Outcome);
        Assert.Equal(ContractErrorCategory.DependencyFailure, result.Error?.Category);
        Assert.True(result.Error?.Retryable);
        Assert.Equal("The authentication decision is temporarily unavailable.", result.Error?.Message);
        var signal = Assert.Single(harness.Telemetry.Snapshot());
        Assert.Equal(D13ContractNames.GetTrustedActorContext, signal.Operation);
        Assert.Equal(ContractOutcome.Failed.ToString(), signal.Outcome);
        Assert.Equal("request-actor", signal.RequestId);
        Assert.Equal("correlation-actor", signal.CorrelationId);
    }

    [Fact]
    public void ReferenceProviderRejectsMalformedCanonicalRecordsAtRegistration()
    {
        var harness = new D13Harness();
        var valid = new ReferenceAuthenticationRecord("malformed-reference", D13Harness.Customer,
            "actor-customer-a", "CUSTOMER", DateTimeOffset.UnixEpoch, "authentication-context");
        var malformedRecords = new[]
        {
            valid with { AuthenticationReference = "malformed-reference\n" },
            valid with { CustomerId = new string('c', 129) },
            valid with { ActorId = "actor\u001f" },
            valid with { ActorType = "PROVIDER_NATIVE_ACTOR" },
            valid with { AuthenticatedAt = default },
            valid with { AuthenticationContextId = " authentication-context" },
        };

        foreach (var record in malformedRecords)
        {
            _ = Assert.Throws<ArgumentException>(() => harness.Provider.Register(record));
        }
    }

    [Fact]
    public async Task ApplicationRejectsMalformedCanonicalProviderResolutionsWithoutNativeLeakage()
    {
        var validActor = new ActorContext("actor-customer-a", "CUSTOMER", DateTimeOffset.UnixEpoch,
            "authentication-context");
        var malformedResolutions = new[]
        {
            null!,
            AuthenticationResolution.Succeeded(validActor with { ActorId = "provider-native\nactor" },
                D13Harness.Customer),
            AuthenticationResolution.Succeeded(validActor with { AuthenticationContextId = string.Empty },
                D13Harness.Customer),
            AuthenticationResolution.Succeeded(validActor with { AuthenticatedAt = default },
                D13Harness.Customer),
            AuthenticationResolution.Succeeded(validActor with { ActorType = "PROVIDER_NATIVE_ACTOR" },
                D13Harness.Customer),
            AuthenticationResolution.Succeeded(validActor, "provider-native\u001fcustomer"),
            new AuthenticationResolution(validActor, D13Harness.Customer,
                "provider-native-sensitive-code", null, false),
            new AuthenticationResolution(validActor, D13Harness.Customer, null, null, true),
        };

        foreach (var resolution in malformedResolutions)
        {
            var harness = new D13Harness();
            var app = new CustomerIdentityApplication(new StubCustomerAuthenticationProvider(resolution),
                harness.Repository, harness.Telemetry, harness.Clock, harness.SessionLifecycle);

            var result = await app.GetTrustedActorContextAsync(harness.GetActor());

            Assert.Equal(ContractOutcome.Rejected, result.Outcome);
            Assert.Equal(ContractErrorCategory.AuthenticationRequired, result.Error?.Category);
            Assert.Equal("identity.authentication.malformed", result.Error?.Code);
            Assert.Null(result.Data);
            var wire = JsonSerializer.Serialize(result, ContractJson.Options);
            Assert.DoesNotContain("provider-native", wire, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ApplicationAcceptsOnlyExistingCanonicalActorTypeVocabulary()
    {
        foreach (var actorType in new[] { "CUSTOMER", "ADVISOR", "ADMINISTRATOR", "PROFESSIONAL" })
        {
            var harness = new D13Harness();
            var actor = new ActorContext("canonical-actor", actorType, DateTimeOffset.UnixEpoch,
                "canonical-authentication-context");
            var app = new CustomerIdentityApplication(
                new StubCustomerAuthenticationProvider(
                    AuthenticationResolution.Succeeded(actor, D13Harness.Customer)),
                harness.Repository, harness.Telemetry, harness.Clock, harness.SessionLifecycle);

            var result = await app.GetTrustedActorContextAsync(harness.GetActor());

            Assert.Equal(ContractOutcome.Success, result.Outcome);
            Assert.Equal(actorType, result.Data?.ActorType);
        }
    }

    [Fact]
    public async Task ProviderFailureCodeIsCanonicalizedWithoutNativeLeakage()
    {
        var harness = new D13Harness();
        var resolution = AuthenticationResolution.Failed("provider-native-sensitive-code",
            ContractErrorCategory.DependencyFailure, retryable: true);
        var app = new CustomerIdentityApplication(new StubCustomerAuthenticationProvider(resolution),
            harness.Repository, harness.Telemetry, harness.Clock, harness.SessionLifecycle);

        var result = await app.GetTrustedActorContextAsync(harness.GetActor());

        Assert.Equal("identity.authentication.provider-failed", result.Error?.Code);
        Assert.DoesNotContain("provider-native", JsonSerializer.Serialize(result, ContractJson.Options),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrustedActorResultDoesNotGrantAuthorizationOrConsentAndWorkloadRemainsDistinct()
    {
        var harness = new D13Harness();
        var result = await harness.App.GetTrustedActorContextAsync(harness.GetActor());

        var actor = Assert.IsType<ActorContext>(result.Data);
        Assert.NotEqual(actor.ActorId, harness.Security.Workload.WorkloadIdentityId);
        var wire = JsonSerializer.Serialize(actor, ContractJson.Options);
        Assert.DoesNotContain("authorization", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consent", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workload", wire, StringComparison.OrdinalIgnoreCase);
    }
}
