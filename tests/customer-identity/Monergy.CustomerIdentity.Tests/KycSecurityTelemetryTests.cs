using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.CustomerIdentity.Infrastructure;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class KycSecurityTelemetryTests
{
    [Fact]
    public async Task CanonicalKycDeliveryIsIdempotentAndLaterResultsPreserveHistory()
    {
        var harness = new D13Harness();
        await harness.CreateCustomerAsync();
        var firstRequest = harness.RecordKyc();
        var first = await harness.App.RecordKycResultAsync(firstRequest);
        var replay = await harness.App.RecordKycResultAsync(firstRequest);
        harness.Clock.Now = harness.Clock.Now.AddMinutes(5);
        var later = await harness.App.RecordKycResultAsync(
            harness.RecordKyc("kyc-2", "verification-2", "REFERENCE_REVIEW"));

        Assert.Equal(first.Data, replay.Data);
        Assert.Equal(2, later.Data?.Sequence);
        Assert.Equal(2, harness.Repository.KycAuthoritativeEffectCount);
        var history = harness.Repository.GetKycHistory(D13Harness.Customer);
        Assert.Equal(["verification-1", "verification-2"],
            history.Select(item => item.VerificationId).ToArray());

        var callerCopy = history.ToArray();
        callerCopy[0] = callerCopy[0] with { StatusCode = "CALLER_CHANGE" };
        Assert.Equal("REFERENCE_VERIFIED",
            harness.Repository.GetKycHistory(D13Harness.Customer)[0].StatusCode);
    }

    [Fact]
    public async Task KycSemanticKeyReuseAndCrossCustomerAttemptAreRejected()
    {
        var harness = new D13Harness();
        await harness.CreateCustomerAsync();
        await harness.App.RecordKycResultAsync(harness.RecordKyc());
        var semanticConflict = await harness.App.RecordKycResultAsync(
            harness.RecordKyc(statusCode: "REFERENCE_REVIEW"));
        var crossCustomerRequest = harness.RecordKyc("kyc-cross",
            security: D13Harness.SecurityFor("customer-b")) with
        {
            RequestId = "request-kyc-cross-customer",
            CorrelationId = "correlation-kyc-cross-customer",
        };
        var crossCustomer = await harness.App.RecordKycResultAsync(crossCustomerRequest);

        Assert.Equal(ContractErrorCategory.DuplicateRequest, semanticConflict.Error?.Category);
        Assert.Equal(ContractErrorCategory.AccessDenied, crossCustomer.Error?.Category);
        Assert.Equal(1, harness.Repository.KycAuthoritativeEffectCount);
        Assert.Contains(harness.Telemetry.Snapshot(), signal =>
            signal.Operation == D13ContractNames.RecordKycResult &&
            signal.Outcome == ContractOutcome.Rejected.ToString() &&
            signal.RequestId == "request-kyc-cross-customer" &&
            signal.CorrelationId == "correlation-kyc-cross-customer" &&
            signal.CustomerId == D13Harness.Customer);
    }

    [Fact]
    public async Task NullAndMalformedTrustedContextsFailClosedBeforeOwnerStateAccess()
    {
        var harness = new D13Harness();
        var request = harness.RecordKyc();
        var nullContext = request with { Security = null! };
        var malformed = request with
        {
            Security = request.Security with
            {
                Workload = request.Security.Workload with { WorkloadIdentityId = string.Empty },
            },
        };

        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.App.RecordKycResultAsync(nullContext)).Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.App.RecordKycResultAsync(malformed)).Error?.Category);
        Assert.Empty(harness.Repository.GetKycHistory(D13Harness.Customer));
    }

    [Fact]
    public async Task CorrelationTelemetryContainsOnlyBoundedIdentifiersAndOutcomes()
    {
        var harness = new D13Harness();
        await harness.CreateCustomerAsync();
        await harness.App.GetTrustedActorContextAsync(harness.GetActor());
        await harness.App.RecordKycResultAsync(harness.RecordKyc());

        var signals = harness.Telemetry.Snapshot();
        Assert.Equal(3, signals.Length);
        Assert.Contains(signals, signal => signal.RequestId == "request-register");
        Assert.Contains(signals, signal => signal.RequestId == "request-actor");
        Assert.Contains(signals, signal => signal.RequestId == "request-kyc");
        Assert.Contains(signals, signal => signal.CorrelationId == "correlation-register");
        Assert.Contains(signals, signal => signal.CorrelationId == "correlation-actor");
        Assert.Contains(signals, signal => signal.CorrelationId == "correlation-kyc");
        var wire = JsonSerializer.Serialize(signals, ContractJson.Options);
        Assert.DoesNotContain(D13Harness.DisplayName, wire, StringComparison.Ordinal);
        Assert.DoesNotContain(D13Harness.AuthenticationReference, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCE_VERIFIED", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("verification-1", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeTelemetryIdentifiersAreRejectedWithoutTelemetryContamination()
    {
        var harness = new D13Harness();
        var valid = harness.Get();
        var unsafeRequests = new[]
        {
            valid with { RequestId = "request\nunsafe" },
            valid with { RequestId = new string('r', 257) },
            valid with { RequestId = " request-trim" },
            valid with { CorrelationId = "correlation\u001funsafe" },
            valid with { CorrelationId = new string('c', 257) },
            valid with { CorrelationId = "correlation-trim " },
            valid with
            {
                Security = D13Harness.SecurityFor("customer\nunsafe"),
                Payload = new("customer\nunsafe"),
            },
            valid with
            {
                Security = D13Harness.SecurityFor(new string('c', 129)),
                Payload = new(new string('c', 129)),
            },
            valid with
            {
                Security = D13Harness.SecurityFor(" customer-trim"),
                Payload = new(" customer-trim"),
            },
        };

        foreach (var request in unsafeRequests)
        {
            var result = await harness.App.GetCustomerAsync(request);
            Assert.Equal(ContractOutcome.Rejected, result.Outcome);
        }

        Assert.Empty(harness.Telemetry.Snapshot());
    }

    [Fact]
    public void TelemetrySinkDefensivelyDropsUnsafeIdentifiers()
    {
        var telemetry = new InMemoryCustomerIdentityTelemetry(D13Harness.Configuration());
        telemetry.Record(new(D13ContractNames.GetCustomer, ContractOutcome.Rejected.ToString(),
            "customer\nunsafe", "request", "correlation"));
        telemetry.Record(new(D13ContractNames.GetCustomer, ContractOutcome.Rejected.ToString(),
            "customer", new string('r', 257), "correlation"));
        telemetry.Record(new(D13ContractNames.GetCustomer, ContractOutcome.Rejected.ToString(),
            "customer", "request", " correlation"));

        Assert.Empty(telemetry.Snapshot());
    }

    [Fact]
    public void TelemetrySinkDropsControlCharacterAndUnknownOperations()
    {
        var telemetry = new InMemoryCustomerIdentityTelemetry(D13Harness.Configuration());
        telemetry.Record(new("GetCustomer\n", ContractOutcome.Rejected.ToString(),
            "customer", "request", "correlation"));
        telemetry.Record(new("UnknownCustomerOperation", ContractOutcome.Rejected.ToString(),
            "customer", "request", "correlation"));

        Assert.Empty(telemetry.Snapshot());
    }

    [Fact]
    public void TelemetrySinkDropsInvalidOutcomes()
    {
        var telemetry = new InMemoryCustomerIdentityTelemetry(D13Harness.Configuration());
        telemetry.Record(new(D13ContractNames.GetCustomer, "Rejected\n",
            "customer", "request", "correlation"));
        telemetry.Record(new(D13ContractNames.GetCustomer, "Succeeded",
            "customer", "request", "correlation"));

        Assert.Empty(telemetry.Snapshot());
    }

    [Fact]
    public async Task UnsafeOrUnknownRequestContractNameIsRejectedWithoutTelemetryContamination()
    {
        var harness = new D13Harness();
        var invalidRequests = new[]
        {
            harness.Get() with { ContractName = "GetCustomer\n" },
            harness.Get() with { ContractName = "UnknownCustomerOperation" },
        };

        foreach (var request in invalidRequests)
        {
            var result = await harness.App.GetCustomerAsync(request);
            Assert.Equal(ContractOutcome.Rejected, result.Outcome);
            Assert.Equal(ContractErrorCategory.UnsupportedOperation, result.Error?.Category);
        }

        Assert.Empty(harness.Telemetry.Snapshot());
    }

    [Fact]
    public void ReferenceAdaptersAreGuardedToLocalAndCiEphemeralExecution()
    {
        var blocked = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true",
            ["Monergy:ExecutionZone"] = "PERSISTENT",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ReferenceCustomerAuthenticationProvider(blocked));
        Assert.Throws<InvalidOperationException>(() => new InMemoryCustomerIdentityRepository(blocked));
        Assert.Throws<InvalidOperationException>(() => new InMemoryCustomerIdentityTelemetry(blocked));
        _ = new ReferenceCustomerAuthenticationProvider(D13Harness.Configuration("CI_EPHEMERAL"));
    }
}
