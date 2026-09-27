using System.Text.Json;
using Monergy.Contracts;
using Xunit;

namespace Monergy.IntegrationGateway.Tests;

public sealed class IntegrationGatewayContractTests
{
    [Fact]
    public void D06CatalogRealizesExactContractSet()
    {
        Assert.Equal(["CID-015", "CID-016", "CID-017", "CID-018"],
            D06ContractCatalog.All.Select(item => item.Id));
        Assert.Equal(["INTEGRATION", "QUERY", "EVENT", "EVENT"],
            D06ContractCatalog.All.Select(item => item.Type));
    }

    [Fact]
    public void ContractJsonRejectsUnknownMembers()
    {
        const string json = """
            {"contractName":"ExecuteProviderRequest","contractVersion":"1.0.0","requestId":"r","correlationId":"c",
             "causationId":null,"security":{},"idempotencyKey":"k","payload":{},"providerSecret":"must-not-pass"}
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ContractRequest<ExecuteProviderRequest>>(json, ContractJson.Options));
    }

    [Fact]
    public async Task WrongContractVersionIsRejectedWithoutExecution()
    {
        var harness = new GatewayHarness();
        var request = harness.Execute() with { ContractVersion = "2.0.0" };
        var result = await harness.App.ExecuteAsync(request);
        Assert.Equal(ContractErrorCategory.UnsupportedOperation, result.Error!.Category);
        Assert.Equal(0, harness.Connector.ExecutionCount);
    }
}
