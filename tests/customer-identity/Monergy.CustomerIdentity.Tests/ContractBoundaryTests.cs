using System.Text.Json;
using System.Text.Json.Nodes;
using Monergy.Contracts;
using Xunit;

namespace Monergy.CustomerIdentity.Tests;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void D14CatalogPreservesCid001AdvancesCid002AndRealizesOnlyCid005Producer()
    {
        Assert.Equal(["CID-001", "CID-002", "CID-005"],
            D14ContractCatalog.All.Select(item => item.Id).ToArray());
        Assert.Equal("PRESERVED_D13", D14ContractCatalog.All[0].Treatment);
        Assert.Equal("ADVANCED_CURRENT_SESSION_TRUST", D14ContractCatalog.All[1].Treatment);
        Assert.Equal("NEWLY_REALIZED_PRODUCER", D14ContractCatalog.All[2].Treatment);
        Assert.All(D14ContractCatalog.All, item => Assert.Equal("Customer & Identity Service", item.Owner));
        Assert.DoesNotContain(D14ContractCatalog.All, item => item.Id is "CID-006" or "CID-007");
        Assert.Equal(D13ContractNames.GetCustomer, D14ContractCatalog.All[0].Name);
        Assert.Equal(D13ContractNames.GetTrustedActorContext, D14ContractCatalog.All[1].Name);
        Assert.Equal("CustomerIdentityChanged", D14ContractNames.CustomerIdentityChanged);
    }

    [Fact]
    public void D13CatalogHasExactlyCid001ThroughCid004AndLeavesD03CatalogIsolated()
    {
        Assert.Equal(["CID-001", "CID-002", "CID-003", "CID-004"],
            D13ContractCatalog.All.Select(item => item.Id).ToArray());
        Assert.All(D13ContractCatalog.All, item => Assert.Equal("Customer & Identity Service", item.Owner));
        Assert.Equal(14, Vs02ContractCatalog.All.Count);
        Assert.DoesNotContain(Vs02ContractCatalog.All, item =>
            D13ContractCatalog.All.Any(d13 => d13.Id == item.Id));
    }

    [Fact]
    public void Cid001ThroughCid004RequestsRoundTripStrictlyAtCurrentVersion()
    {
        var harness = new D13Harness();
        RoundTrip(harness.Get());
        RoundTrip(harness.GetActor());
        RoundTrip(harness.Register());
        RoundTrip(harness.RecordKyc());

        Assert.Equal("GetCustomer", D13ContractNames.GetCustomer);
        Assert.Equal("GetTrustedActorContext", D13ContractNames.GetTrustedActorContext);
        Assert.Equal("Register/UpdateCustomer", D13ContractNames.RegisterOrUpdateCustomer);
        Assert.Equal("RecordKycResult", D13ContractNames.RecordKycResult);
    }

    [Fact]
    public async Task MissingContextNullSecurityInvalidVersionAndNullPayloadFailClosed()
    {
        var harness = new D13Harness();
        var valid = harness.Get();
        var nullSecurity = valid with { Security = null! };
        var malformedSecurity = valid with
        {
            Security = valid.Security with { Actor = valid.Security.Actor with { ActorId = string.Empty } },
        };
        var invalidVersion = valid with { ContractVersion = "2.0.0" };
        var nullPayload = valid with { Payload = null! };

        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.App.GetCustomerAsync(nullSecurity)).Error?.Category);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.App.GetCustomerAsync(malformedSecurity)).Error?.Category);
        Assert.Equal(ContractErrorCategory.UnsupportedOperation,
            (await harness.App.GetCustomerAsync(invalidVersion)).Error?.Category);
        Assert.Equal(ContractErrorCategory.ValidationError,
            (await harness.App.GetCustomerAsync(nullPayload)).Error?.Category);

        var node = JsonNode.Parse(JsonSerializer.Serialize(valid, ContractJson.Options))!.AsObject();
        Assert.True(node.Remove("security"));
        var deserialized = node.Deserialize<ContractRequest<GetCustomer>>(ContractJson.Options);
        Assert.Equal(ContractErrorCategory.AuthenticationRequired,
            (await harness.App.GetCustomerAsync(deserialized!)).Error?.Category);
    }

    [Fact]
    public void UnknownJsonMembersAndProviderNativeKycShapesAreRejected()
    {
        var harness = new D13Harness();
        var customerNode = JsonNode.Parse(JsonSerializer.Serialize(harness.Get(), ContractJson.Options))!.AsObject();
        customerNode["unknownMember"] = true;
        Assert.Throws<JsonException>(() =>
            customerNode.Deserialize<ContractRequest<GetCustomer>>(ContractJson.Options));

        var kycNode = JsonNode.Parse(JsonSerializer.Serialize(harness.RecordKyc(), ContractJson.Options))!.AsObject();
        kycNode["payload"]!.AsObject()["providerResponse"] = new JsonObject { ["decision"] = "native" };
        Assert.Throws<JsonException>(() =>
            kycNode.Deserialize<ContractRequest<RecordKycResult>>(ContractJson.Options));
    }

    [Fact]
    public void CustomerIdentityWireDtosExposeOnlyCanonicalProviderNeutralFields()
    {
        var contractTypes = new[]
        {
            typeof(GetCustomer), typeof(CustomerProjection), typeof(GetTrustedActorContext),
            typeof(RegisterOrUpdateCustomer), typeof(RecordKycResult), typeof(KycVerification),
        };
        var propertyNames = contractTypes.SelectMany(type => type.GetProperties())
            .Select(property => property.Name).ToArray();
        var forbiddenFragments = new[] { "Claim", "Credential", "Bearer", "Password", "Mfa", "ProviderPayload" };

        Assert.DoesNotContain(propertyNames, name => forbiddenFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static void RoundTrip<T>(ContractRequest<T> request)
    {
        var json = JsonSerializer.Serialize(request, ContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize<ContractRequest<T>>(json, ContractJson.Options);
        Assert.Equal(request, roundTrip);
        Assert.Equal(ContractGuard.CurrentVersion, roundTrip!.ContractVersion);
    }
}
