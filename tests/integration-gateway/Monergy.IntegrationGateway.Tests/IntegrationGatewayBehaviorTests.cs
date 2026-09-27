using System.Collections.Immutable;
using Monergy.Contracts;
using Monergy.Services.IntegrationGateway.Domain;
using Monergy.Services.IntegrationGateway.Infrastructure;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class IntegrationGatewayBehaviorTests
{
    [Fact]
    public async Task SuccessfulExecutionPublishesCanonicalResultAndSupportsStatusQuery()
    {
        var harness = new GatewayHarness();

        var executed = await harness.App.ExecuteAsync(harness.Execute());
        var status = await harness.App.GetStatusAsync(harness.Status(executed.Data!.ProviderOperationId));

        Assert.Equal(ContractOutcome.Success, executed.Outcome);
        Assert.Equal(ProviderOperationState.Succeeded, executed.Data.State);
        Assert.StartsWith("canonical-result-", executed.Data.CanonicalResultReferenceId, StringComparison.Ordinal);
        Assert.Equal(executed.Data, status.Data);
        Assert.Contains(harness.Events.Messages, item => item is DomainEvent<ProviderResultReceivedPayload>);
    }

    [Fact]
    public async Task ReplayIsAuthorizedAgainAndDoesNotExecuteConnectorTwice()
    {
        var harness = new GatewayHarness();
        var first = await harness.App.ExecuteAsync(harness.Execute("same-key"));
        var replay = await harness.App.ExecuteAsync(harness.Execute("same-key"));

        Assert.Equal(first.Data, replay.Data);
        Assert.Equal(1, harness.Connector.ExecutionCount);
        Assert.Equal(3, harness.Authorization.EvaluationCount);
        Assert.Equal(3, harness.Consent.EvaluationCount);
        Assert.Contains(harness.Telemetry.Signals, signal => signal.Outcome == IdempotencyDisposition.Replay.ToString());
    }

    [Fact]
    public async Task ReplayIsDeniedAfterConsentRevocation()
    {
        var harness = new GatewayHarness();
        var first = await harness.App.ExecuteAsync(harness.Execute("revoked-replay"));
        harness.Grant(consentRevoked: true);

        var replay = await harness.App.ExecuteAsync(harness.Execute("revoked-replay"));

        Assert.Equal(ContractOutcome.Success, first.Outcome);
        Assert.Equal(ContractOutcome.Rejected, replay.Outcome);
        Assert.Equal(ContractErrorCategory.ConsentRevoked, replay.Error!.Category);
        Assert.Equal(1, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task IdempotencyIdentityRejectsDifferentContent()
    {
        var harness = new GatewayHarness();
        await harness.App.ExecuteAsync(harness.Execute("conflict"));

        var conflict = await harness.App.ExecuteAsync(harness.Execute("conflict",
            fields: [new("account-reference", "different-opaque-account")]));

        Assert.Equal(ContractOutcome.Rejected, conflict.Outcome);
        Assert.Equal(ContractErrorCategory.Conflict, conflict.Error!.Category);
    }

    [Fact]
    public async Task TransientFailureRetriesWithoutDelayAndRechecksAccess()
    {
        var harness = new GatewayHarness();
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.TransientFailure,
            "integration.synthetic.transient", true));
        harness.Connector.Enqueue(ConnectorAttemptResult.Success("canonical-final"));

        var result = await harness.App.ExecuteAsync(harness.Execute("retry"));

        Assert.Equal(ProviderOperationState.Succeeded, result.Data!.State);
        Assert.Equal(2, result.Data.AttemptCount);
        Assert.Equal(3, harness.Authorization.EvaluationCount);
        Assert.Equal(3, harness.Consent.EvaluationCount);
        Assert.Equal(2, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task ConsentRevokedBetweenAttemptsStopsRetryBeforeSecondConnectorCall()
    {
        var harness = new GatewayHarness();
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.TransientFailure,
            "integration.synthetic.transient", true));
        harness.Connector.AfterExecution = count =>
        {
            if (count == 1) { harness.Grant(consentRevoked: true); }
        };

        var result = await harness.App.ExecuteAsync(harness.Execute("revoke-between-attempts"));

        Assert.Equal(ProviderOperationState.Failed, result.Data!.State);
        Assert.Equal(ContractErrorCategory.ConsentRevoked, result.Data.Failure!.Category);
        Assert.Equal(1, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task ConcurrentDuplicatesShareOneLogicalExecution()
    {
        var harness = new GatewayHarness();
        var connector = new BlockingConnector();
        harness.Registry.Register(connector);
        var first = harness.App.ExecuteAsync(harness.Execute("concurrent", connector.ConnectorId));
        await connector.Entered.Task;
        var duplicate = harness.App.ExecuteAsync(harness.Execute("concurrent", connector.ConnectorId));
        connector.Release.TrySetResult();

        var results = await Task.WhenAll(first, duplicate);

        Assert.Equal(1, connector.ExecutionCount);
        Assert.Equal(results[0].Data, results[1].Data);
        Assert.Contains(harness.Telemetry.Signals,
            signal => signal.Outcome == IdempotencyDisposition.ConcurrentReplay.ToString());
    }

    [Theory]
    [InlineData(ConnectorAttemptOutcome.RateLimited, ProviderOperationState.RateLimited, ContractErrorCategory.RateLimited)]
    [InlineData(ConnectorAttemptOutcome.UnknownOutcome, ProviderOperationState.UnknownOutcome, ContractErrorCategory.TemporarilyUnavailable)]
    [InlineData(ConnectorAttemptOutcome.MalformedProviderResponse, ProviderOperationState.Failed, ContractErrorCategory.ProcessingFailed)]
    [InlineData(ConnectorAttemptOutcome.PermanentFailure, ProviderOperationState.Failed, ContractErrorCategory.DependencyFailure)]
    public async Task ConnectorFailuresAreExplicitlyClassified(
        ConnectorAttemptOutcome outcome, ProviderOperationState expectedState, ContractErrorCategory expectedCategory)
    {
        var harness = new GatewayHarness(maximumAttempts: 1);
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(outcome, "integration.synthetic.failure", false));

        var result = await harness.App.ExecuteAsync(harness.Execute("failure-" + outcome));

        Assert.Equal(ContractOutcome.Success, result.Outcome);
        Assert.Equal(expectedState, result.Data!.State);
        Assert.Equal(expectedCategory, result.Data.Failure!.Category);
        Assert.Contains(harness.Events.Messages, item => item is DomainEvent<ProviderOperationFailedPayload>);
    }

    [Fact]
    public async Task UnknownOutcomeIsReconciledThroughOperationStatus()
    {
        var harness = new GatewayHarness(maximumAttempts: 1);
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.UnknownOutcome,
            "integration.synthetic.timeout", true));
        var executed = await harness.App.ExecuteAsync(harness.Execute("unknown-outcome"));

        var reconciled = await harness.App.GetStatusAsync(harness.Status(executed.Data!.ProviderOperationId));

        Assert.Equal(ProviderOperationState.UnknownOutcome, reconciled.Data!.State);
        Assert.True(reconciled.Data.Failure!.Retryable);
        Assert.Equal(1, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task CircuitOpensAfterThresholdAndHealthIsDerived()
    {
        var harness = new GatewayHarness(maximumAttempts: 1, circuitThreshold: 2);
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.PermanentFailure, "failure-1", false));
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.PermanentFailure, "failure-2", false));
        await harness.App.ExecuteAsync(harness.Execute("circuit-1"));
        await harness.App.ExecuteAsync(harness.Execute("circuit-2"));

        var rejectedByCircuit = await harness.App.ExecuteAsync(harness.Execute("circuit-3"));
        var health = Assert.Single(harness.App.GetConnectorHealth());

        Assert.Equal("integration.circuit.open", rejectedByCircuit.Data!.Failure!.Code);
        Assert.Contains(harness.Telemetry.Signals,
            signal => signal.Outcome == ConnectorAttemptOutcome.CircuitRejected.ToString());
        Assert.Equal(ConnectorHealthState.Unavailable, health.State);
        Assert.Equal(2, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task ConnectorCircuitStateIsIsolated()
    {
        var harness = new GatewayHarness(maximumAttempts: 1, circuitThreshold: 1);
        var healthy = new ReferenceProviderConnector("second-reference", [GatewayHarness.Operation], GatewayHarness.Config());
        harness.Registry.Register(healthy);
        harness.Connector.Enqueue(ConnectorAttemptResult.Failure(ConnectorAttemptOutcome.PermanentFailure,
            "integration.synthetic.failure", false));

        await harness.App.ExecuteAsync(harness.Execute("isolation-failure"));
        var second = await harness.App.ExecuteAsync(harness.Execute("isolation-success", healthy.ConnectorId));
        var health = harness.App.GetConnectorHealth();

        Assert.Equal(ProviderOperationState.Succeeded, second.Data!.State);
        Assert.Equal(ConnectorHealthState.Unavailable,
            health.Single(item => item.ConnectorId == harness.Connector.ConnectorId).State);
        Assert.Equal(ConnectorHealthState.Healthy,
            health.Single(item => item.ConnectorId == healthy.ConnectorId).State);
    }

    [Fact]
    public async Task MissingOrDeniedSecurityFailsClosed()
    {
        var harness = new GatewayHarness();
        var missing = await harness.App.ExecuteAsync(harness.Execute() with { Security = null! });
        harness.Grant(authorizationRevoked: true);
        var denied = await harness.App.ExecuteAsync(harness.Execute("denied"));

        Assert.Equal(ContractErrorCategory.AuthenticationRequired, missing.Error!.Category);
        Assert.Equal(ContractErrorCategory.AccessDenied, denied.Error!.Category);
        Assert.Equal(0, harness.Connector.ExecutionCount);
    }

    [Fact]
    public async Task MissingConsentFailsClosedAfterAuthorization()
    {
        var harness = new GatewayHarness();
        var security = harness.Security with
        {
            Access = harness.Security.Access with { ConsentReferenceId = null }
        };
        harness.Authorization.SetGrant(new(security, harness.Clock.Now.AddHours(1), false));

        var result = await harness.App.ExecuteAsync(harness.Execute("missing-consent") with { Security = security });

        Assert.Equal(ContractErrorCategory.ConsentRequired, result.Error!.Category);
        Assert.Equal(0, harness.Connector.ExecutionCount);
    }

    [Fact]
    public void ConnectorBoundaryExposesOnlyCanonicalGatewayTypes()
    {
        var propertyNames = typeof(ConnectorExecutionRequest).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(["ProviderOperationId", "CustomerId", "Operation", "Fields", "CorrelationId", "Attempt"], propertyNames);
        Assert.DoesNotContain(propertyNames, name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TelemetryContainsSemanticMetadataButNotRequestValuesOrSecurityReferences()
    {
        var harness = new GatewayHarness();
        const string protectedValue = "protected-financial-value";
        await harness.App.ExecuteAsync(harness.Execute(fields: [new("financial-value", protectedValue)]));

        var serialized = System.Text.Json.JsonSerializer.Serialize(harness.Telemetry.Signals);
        Assert.DoesNotContain(protectedValue, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Security.Access.ConsentReferenceId!, serialized, StringComparison.Ordinal);
        Assert.Contains("integration.connector.attempt", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "reference-source-facts", GatewayHarness.Operation, ContractErrorCategory.PreconditionFailed)]
    [InlineData("key", "unknown", GatewayHarness.Operation, ContractErrorCategory.UnsupportedOperation)]
    [InlineData("key", "reference-source-facts", "UNSUPPORTED", ContractErrorCategory.UnsupportedOperation)]
    public async Task InvalidRoutingOrIdempotencyIsRejected(
        string key, string connector, string operation, ContractErrorCategory category)
    {
        var harness = new GatewayHarness();
        var result = await harness.App.ExecuteAsync(harness.Execute(key, connector, operation));
        Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        Assert.Equal(category, result.Error!.Category);
    }

    [Fact]
    public async Task ExpiredConsentFailsClosedBeforeConnectorExecution()
    {
        var harness = new GatewayHarness();
        harness.Grant(consentExpiresAt: harness.Clock.Now);
        var result = await harness.App.ExecuteAsync(harness.Execute());
        Assert.Equal(ContractErrorCategory.ConsentExpired, result.Error!.Category);
        Assert.Equal(0, harness.Connector.ExecutionCount);
    }

    [Fact]
    public void RegistryRejectsDuplicateConnectorIdentity()
    {
        var harness = new GatewayHarness();
        Assert.Throws<InvalidOperationException>(() => harness.Registry.Register(
            new ReferenceProviderConnector("reference-source-facts", [GatewayHarness.Operation], GatewayHarness.Config())));
    }

    [Theory]
    [InlineData("QA")]
    [InlineData("UAT")]
    [InlineData("PRODUCTION")]
    public void ReferenceAdaptersFailClosedOutsideLocalAndCi(string zone)
    {
        var configuration = GatewayHarness.Config(zone);
        Assert.Throws<InvalidOperationException>(() => new ReferenceConnectorRegistry(configuration));
        Assert.Throws<InvalidOperationException>(() =>
            new ReferenceProviderConnector("reference", [GatewayHarness.Operation], configuration));
        Assert.Throws<InvalidOperationException>(() => new ReferenceGatewayAuthorizationPolicy(configuration, new TestClock()));
        Assert.Throws<InvalidOperationException>(() => new ReferenceConsentDecisionPort(configuration, new TestClock()));
        Assert.Throws<InvalidOperationException>(() => new InMemoryGatewayOperationRepository(configuration));
        Assert.Throws<InvalidOperationException>(() => new InMemoryGatewayEventSink(configuration));
        Assert.Throws<InvalidOperationException>(() => new InMemoryGatewayTelemetry(configuration));
    }
}
