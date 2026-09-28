using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Domain;
using Monergy.Services.SearchRetrieval.Infrastructure;

namespace Monergy.SearchRetrieval.Tests;

internal sealed class SearchTestContext
{
    public SearchTestContext()
    {
        Configuration = BuildConfiguration("LOCAL");
        CustomerA = Security("customer-a", "authorization-a", "actor-a", "workload-a");
        CustomerB = Security("customer-b", "authorization-b", "actor-b", "workload-b");
        AlternateA = Security("customer-a", "authorization-a2", "actor-a2", "workload-a2");
        Records =
        [
            Record("document-a", "customer-a", "authorization-a", SearchResultType.Document,
                "Annual salary statement", "salary evidence April", SearchAuthority.EvidenceService,
                new("DocumentVersion", "document-version-a", "evidence-a", "evidence-provenance-a", null)),
            Record("financial-a", "customer-a", "authorization-a", SearchResultType.Financial,
                "Normalized salary", "salary INR 125000", SearchAuthority.FinancialProfileService,
                new("FinancialFact", "financial-fact-a", "financial-profile-a", "financial-provenance-a", null)),
            Record("calculation-a", "customer-a", "authorization-a", SearchResultType.Calculation,
                "Savings ratio", "deterministic savings calculation", SearchAuthority.FinancialRulesService,
                new("FinancialCalculation", "calculation-a", "calculation-result-a", "financial-provenance-a", "lineage-a")),
            Record("document-a-hidden", "customer-a", "authorization-a2", SearchResultType.Document,
                "Private tax evidence", "tax evidence", SearchAuthority.EvidenceService,
                new("DocumentVersion", "document-version-hidden", "evidence-hidden", "evidence-provenance-hidden", null)),
            Record("document-b", "customer-b", "authorization-b", SearchResultType.Document,
                "Customer B salary", "salary evidence customer b", SearchAuthority.EvidenceService,
                new("DocumentVersion", "document-version-b", "evidence-b", "evidence-provenance-b", null)),
        ];
        Source = new ReferenceSearchProjectionSource(Configuration, Records);
        Index = new InMemoryDerivedSearchIndex(Configuration);
        Index.Rebuild(Source.ReadAll());
        Authorization = new ReferenceSearchAuthorizationPolicy(Configuration);
        Authorization.Grant(CustomerA);
        Authorization.Grant(CustomerB);
        Authorization.Grant(AlternateA);
        Application = new SearchRetrievalApplication(Index, Source, Authorization);
    }

    public IConfiguration Configuration { get; }
    public TrustedSecurityContext CustomerA { get; }
    public TrustedSecurityContext CustomerB { get; }
    public TrustedSecurityContext AlternateA { get; }
    public ImmutableArray<DerivedSearchRecord> Records { get; }
    public ReferenceSearchProjectionSource Source { get; }
    public InMemoryDerivedSearchIndex Index { get; }
    public ReferenceSearchAuthorizationPolicy Authorization { get; }
    public SearchRetrievalApplication Application { get; }

    public ContractRequest<T> Request<T>(string name, T payload, TrustedSecurityContext? security = null) =>
        new(name, ContractGuard.CurrentVersion, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            null, security ?? CustomerA, null, payload);

    public static IConfiguration BuildConfiguration(string zone) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monergy:ReferenceAdapters"] = "true",
            ["Monergy:ExecutionZone"] = zone,
        }).Build();

    private static TrustedSecurityContext Security(string customer, string authorization, string actor, string workload) =>
        new(new(actor, "HUMAN", DateTimeOffset.UnixEpoch, $"authentication-{actor}"),
            new("search-retrieval", workload), new("AUTHORIZED_SEARCH", null, authorization, customer));

    private static DerivedSearchRecord Record(string id, string customer, string authorization,
        SearchResultType type, string title, string text, string owner, SearchSourceReference source) =>
        new(id, customer, authorization, type, title, text, owner, source);
}
