using System.Text.Json;
using Monergy.Contracts;
using Xunit;

namespace Monergy.Vs02.Tests;

public sealed class FinancialProfileContractCompatibilityTests
{
    public static TheoryData<string, object, Type> ContractPayloads => new()
    {
        { "CID-030", new GetFinancialProfile("profile-001", Vs02TestContext.CustomerId), typeof(GetFinancialProfile) },
        { "CID-031", new NormalizeSourceFacts("profile-001", Vs02TestContext.CustomerId, "processing-001", [], "normalization/1.0.0", DateTimeOffset.UnixEpoch), typeof(NormalizeSourceFacts) },
        { "CID-032", new GetFinancialFact("fact-001", Vs02TestContext.CustomerId), typeof(GetFinancialFact) },
        { "CID-033", new GetFinancialProvenance("provenance-001", Vs02TestContext.CustomerId), typeof(GetFinancialProvenance) },
        { "CID-034", new FinancialFactChangedPayload("profile-001", "fact-001", FinancialObjectTypes.Income, DateTimeOffset.UnixEpoch, "provenance-001", 1), typeof(FinancialFactChangedPayload) },
        { "CID-035", new FinancialFactChangedPayload("profile-001", "fact-001", FinancialObjectTypes.Income, DateTimeOffset.UnixEpoch, "provenance-002", 2), typeof(FinancialFactChangedPayload) },
        { "CID-036", new FinancialProfileChangedPayload("profile-001", Vs02TestContext.CustomerId, DateTimeOffset.UnixEpoch, 2, ["fact-001"]), typeof(FinancialProfileChangedPayload) },
    };

    [Fact]
    public void CatalogPreservesExactlyTheSevenGovernedContractsAndSemantics()
    {
        var expected = new[] { "CID-030", "CID-031", "CID-032", "CID-033", "CID-034", "CID-035", "CID-036" };

        Assert.Equal(expected, D04ContractCatalog.All.Select(contract => contract.Id));
        Assert.All(D04ContractCatalog.All, contract =>
        {
            Assert.Equal("Financial Profile Service", contract.Owner);
            Assert.Equal("Financial Profile Service", contract.Producer);
            Assert.NotEmpty(contract.Consumers);
            Assert.NotEmpty(contract.Authorization);
            Assert.Contains("1.0.0", contract.Versioning, StringComparison.Ordinal);
        });
    }

    [Theory]
    [MemberData(nameof(ContractPayloads))]
    public void EveryProducerAndConsumerPayloadRoundTripsStrictly(string contractId, object payload, Type payloadType)
    {
        var serialized = JsonSerializer.Serialize(payload, payloadType, ContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize(serialized, payloadType, ContractJson.Options);

        Assert.NotNull(roundTrip);
        Assert.Contains(D04ContractCatalog.All, contract => contract.Id == contractId);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedPayloadWithUnknownMemberIsRejected()
    {
        const string malformed = "{\"financialFactId\":\"fact-001\",\"customerId\":\"customer-001\",\"unexpected\":true}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GetFinancialFact>(malformed, ContractJson.Options));
    }

    [Fact]
    public void MachineReadableSchemaContainsExactlyTheD04ContractSet()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "schemas", "financial-profile-authority.schema.json")));
        var ids = document.RootElement.GetProperty("properties").GetProperty("contractId").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Equal(D04ContractCatalog.All.Select(contract => contract.Id), ids);
        Assert.Equal("1.0.0", document.RootElement.GetProperty("properties").GetProperty("contractVersion").GetProperty("const").GetString());
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "repository.manifest.json")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
