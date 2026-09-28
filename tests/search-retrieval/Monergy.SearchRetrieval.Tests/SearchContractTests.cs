using System.Text.Json;
using Monergy.Contracts;
using Xunit;

namespace Monergy.SearchRetrieval.Tests;

public sealed class SearchContractTests
{
    [Fact]
    public void CatalogContainsOnlyTheCanonicalD07ContractSurface()
    {
        Assert.Equal(["CID-042", "CID-043", "CID-044", "CID-046"], D07ContractCatalog.Contracts.Select(item => item.ContractId));
        Assert.Equal([SearchContractNames.SearchDocuments, SearchContractNames.SearchFinancialData,
            SearchContractNames.BuildRetrievalContext, SearchContractNames.IndexRefreshRequested],
            D07ContractCatalog.Contracts.Select(item => item.Name));
        Assert.All(D07ContractCatalog.Contracts, item => Assert.Equal("Search & Retrieval Service", item.Owner));
    }

    [Fact]
    public void ClosedWireSchemaDefinesExactlyFourContracts()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "contracts", "schemas", "authorized-search.schema.json")));
        Assert.Equal(4, schema.RootElement.GetProperty("oneOf").GetArrayLength());
        Assert.False(schema.RootElement.GetProperty("unevaluatedProperties").GetBoolean());
    }

    [Fact]
    public void ContractJsonRejectsUnknownPayloadFields()
    {
        const string json = """
            {"contractName":"SearchDocuments","contractVersion":"1.0.0","requestId":"r","correlationId":"c","causationId":null,"security":{"actor":{"actorId":"a","actorType":"HUMAN","authenticatedAt":"1970-01-01T00:00:00Z","authenticationContextId":"ac"},"workload":{"workloadId":"w","workloadIdentityId":"wi"},"access":{"purpose":"p","consentReferenceId":null,"authorizationContextId":"auth","customerId":"customer"}},"idempotencyKey":null,"payload":{"customerId":"customer","query":"salary","matchMode":"Lexical","limit":20,"unexpected":true}}
            """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ContractRequest<SearchDocuments>>(json, ContractJson.Options));
    }

    [Fact]
    public void EndpointSourceMapsOnlyCanonicalD07Contracts()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "services", "search-retrieval", "SearchRetrievalEndpoints.cs"));
        foreach (var id in new[] { "cid-042", "cid-043", "cid-044", "cid-046" }) Assert.Contains($"/contracts/{id}/v1", source, StringComparison.Ordinal);
        Assert.Equal(4, source.Split("endpoints.MapPost", StringSplitOptions.None).Length - 1);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "repository.manifest.json"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
