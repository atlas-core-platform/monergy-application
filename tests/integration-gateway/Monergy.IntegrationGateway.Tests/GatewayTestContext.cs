using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.IntegrationGateway.Application;
using Monergy.Services.IntegrationGateway.Domain;
using Monergy.Services.IntegrationGateway.Infrastructure;

namespace Monergy.IntegrationGateway.Tests;

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-26T00:00:00Z", CultureInfo.InvariantCulture);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class BlockingConnector : IProviderConnector
{
    public string ConnectorId => "blocking-reference";
    public ImmutableHashSet<string> SupportedOperations { get; } = [GatewayHarness.Operation];
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ExecutionCount { get; private set; }

    public async Task<ConnectorAttemptResult> ExecuteAsync(ConnectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return ConnectorAttemptResult.Success("canonical-blocking-result");
    }
}

internal sealed class GatewayHarness
{
    public const string Customer = "synthetic-customer";
    public const string Operation = "READ_CANONICAL_SOURCE_FACTS";
    public TestClock Clock { get; } = new();
    public TrustedSecurityContext Security { get; }
    public ReferenceGatewayAuthorizationPolicy Authorization { get; }
    public ReferenceConsentDecisionPort Consent { get; }
    public ReferenceProviderConnector Connector { get; }
    public ReferenceConnectorRegistry Registry { get; }
    public InMemoryGatewayOperationRepository Repository { get; }
    public InMemoryGatewayEventSink Events { get; }
    public InMemoryGatewayTelemetry Telemetry { get; }
    public ConnectorRuntimeState RuntimeState { get; }
    public IntegrationGatewayApplication App { get; }

    public static IConfiguration Config(string zone = "LOCAL") => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true",
            ["Monergy:ExecutionZone"] = zone,
        }).Build();

    public GatewayHarness(int maximumAttempts = 2, int circuitThreshold = 2)
    {
        Security = new(new("synthetic-actor", "CUSTOMER", Clock.Now, "authentication"),
            new("integration-gateway", "integration-workload"),
            new("provider-read", "consent-reference", "authorization", Customer));
        Authorization = new(Config(), Clock);
        Consent = new(Config(), Clock);
        Grant();
        Connector = new("reference-source-facts", [Operation], Config());
        Registry = new(Config());
        Registry.Register(Connector);
        Repository = new(Config());
        Events = new(Config());
        Telemetry = new(Config());
        var options = new GatewayExecutionOptions(maximumAttempts, circuitThreshold);
        RuntimeState = new(options);
        App = new(Registry, Authorization, Consent, Repository, Events, RuntimeState, Telemetry, options, Clock);
    }

    public void Grant(bool authorizationRevoked = false, bool consentRevoked = false,
        DateTimeOffset? authorizationExpiresAt = null, DateTimeOffset? consentExpiresAt = null)
    {
        Authorization.SetGrant(new(Security, authorizationExpiresAt ?? Clock.Now.AddHours(1), authorizationRevoked));
        Consent.SetDecision(new(Security.Access.ConsentReferenceId!, Customer, Security.Access.Purpose,
            consentExpiresAt ?? Clock.Now.AddMinutes(30), consentRevoked));
    }

    public ContractRequest<ExecuteProviderRequest> Execute(string key = "gateway-1",
        string connectorId = "reference-source-facts", string operation = Operation,
        ImmutableArray<ProviderRequestField>? fields = null) =>
        new(IntegrationGatewayContractNames.ExecuteProviderRequest, ContractGuard.CurrentVersion,
            "request-" + Guid.NewGuid().ToString("N"), "correlation", null, Security, key,
            new(Customer, connectorId, operation, fields ?? [new("account-reference", "opaque-account")]));

    public ContractRequest<GetProviderOperationStatus> Status(string operationId) =>
        new(IntegrationGatewayContractNames.GetProviderOperationStatus, ContractGuard.CurrentVersion,
            "request-" + Guid.NewGuid().ToString("N"), "correlation", null, Security, null,
            new(Customer, operationId));
}
