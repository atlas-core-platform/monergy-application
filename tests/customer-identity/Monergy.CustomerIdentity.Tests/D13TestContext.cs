using System.Globalization;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

internal sealed class D13TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } =
        DateTimeOffset.Parse("2026-10-04T00:00:00Z", CultureInfo.InvariantCulture);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StubCustomerAuthenticationProvider(AuthenticationResolution resolution)
    : ICustomerAuthenticationProvider
{
    public Task<AuthenticationResolution> ResolveAsync(
        AuthenticationReference authentication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(resolution);
    }
}

internal sealed class D13Harness
{
    public const string Customer = "customer-a";
    public const string DisplayName = "Reference Customer A";
    public const string AuthenticationReference = "reference-auth-customer-a";

    public D13TestClock Clock { get; } = new();
    public TrustedSecurityContext Security { get; } = SecurityFor(Customer);
    public ReferenceCustomerAuthenticationProvider Provider { get; }
    public InMemoryCustomerIdentityRepository Repository { get; }
    public InMemoryCustomerIdentityTelemetry Telemetry { get; }
    public InMemoryTrustedSessionRepository Sessions { get; }
    public InMemoryCustomerIdentityEventSink Events { get; }
    public TrustedSessionLifecycle SessionLifecycle { get; }
    public CustomerIdentityApplication App { get; }

    public D13Harness()
    {
        var configuration = Configuration();
        Provider = new(configuration);
        Repository = new(configuration);
        Telemetry = new(configuration);
        Sessions = new(configuration);
        Events = new(configuration);
        SessionLifecycle = new(Sessions, Events, TrustedSessionPolicy.ReferenceDefault, Clock);
        App = new(Provider, Repository, Telemetry, Clock, SessionLifecycle);
    }

    public static IConfiguration Configuration(string zone = "LOCAL") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true",
            ["Monergy:ExecutionZone"] = zone,
        }).Build();

    public static TrustedSecurityContext SecurityFor(string customerId) =>
        new(new("trusted-caller", "CUSTOMER", DateTimeOffset.UnixEpoch, "trusted-caller-context"),
            new("customer-identity", "customer-identity-reference-workload"),
            new("CUSTOMER_IDENTITY_REFERENCE", null, "trusted-access-context", customerId));

    public ContractRequest<RegisterOrUpdateCustomer> Register(
        string key = "customer-create-1",
        string displayName = DisplayName,
        int? expectedRevision = null,
        TrustedSecurityContext? security = null) =>
        new(D13ContractNames.RegisterOrUpdateCustomer, ContractGuard.CurrentVersion, "request-register",
            "correlation-register", null, security ?? Security, key,
            new(Customer, displayName, expectedRevision));

    public ContractRequest<GetCustomer> Get(
        string customerId = Customer,
        TrustedSecurityContext? security = null) =>
        new(D13ContractNames.GetCustomer, ContractGuard.CurrentVersion, "request-get", "correlation-get",
            null, security ?? Security, null, new(customerId));

    public ContractRequest<GetTrustedActorContext> GetActor(
        string authenticationReference = AuthenticationReference,
        string customerId = Customer,
        TrustedSecurityContext? security = null) =>
        new(D13ContractNames.GetTrustedActorContext, ContractGuard.CurrentVersion, "request-actor",
            "correlation-actor", null, security ?? Security, null,
            new(customerId, authenticationReference));

    public ContractRequest<RecordKycResult> RecordKyc(
        string key = "kyc-1",
        string verificationId = "verification-1",
        string statusCode = "REFERENCE_VERIFIED",
        TrustedSecurityContext? security = null) =>
        new(D13ContractNames.RecordKycResult, ContractGuard.CurrentVersion, "request-kyc",
            "correlation-kyc", null, security ?? Security, key,
            new(Customer, verificationId, statusCode, Clock.Now.AddMinutes(-1)));

    public async Task<CustomerProjection> CreateCustomerAsync()
    {
        var result = await App.RegisterOrUpdateCustomerAsync(Register());
        return Assert.IsType<CustomerProjection>(result.Data);
    }
}
