using System.Collections.Immutable;

namespace Monergy.Contracts;

public sealed record SearchContractDefinition(
    string ContractId,
    string Name,
    string Taxonomy,
    string Owner,
    ImmutableArray<string> Consumers,
    string AuthorityRule);

public static class D07ContractCatalog
{
    public static ImmutableArray<SearchContractDefinition> Contracts { get; } =
    [
        new("CID-042", SearchContractNames.SearchDocuments, "QUERY", "Search & Retrieval Service",
            ["AI Intelligence Service", "Reporting Service"], "Derived authorized search; Evidence remains authority."),
        new("CID-043", SearchContractNames.SearchFinancialData, "QUERY", "Search & Retrieval Service",
            ["AI Intelligence Service", "Reporting Service"], "Derived authorized search; Financial Profile remains authority."),
        new("CID-044", SearchContractNames.BuildRetrievalContext, "QUERY", "Search & Retrieval Service",
            ["AI Intelligence Service"], "Authorized grounded context with source references; no freshness claim."),
        new("CID-046", SearchContractNames.IndexRefreshRequested, "JOB", "Search & Retrieval Service",
            ["Job Management Service"], "Derived-index refresh without authority transfer."),
    ];
}
