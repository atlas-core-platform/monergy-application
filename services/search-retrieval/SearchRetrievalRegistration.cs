using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Infrastructure;

namespace Monergy.Services.SearchRetrieval;

public static class SearchRetrievalRegistration
{
    public static IServiceCollection AddSearchRetrievalReferenceAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        var source = new ReferenceSearchProjectionSource(configuration);
        var index = new InMemoryDerivedSearchIndex(configuration);
        index.Rebuild(source.ReadAll());
        var authorization = new ReferenceSearchAuthorizationPolicy(configuration);
        authorization.Grant(new(
            new("reference-actor", "HUMAN", DateTimeOffset.UnixEpoch, "reference-authentication"),
            new("search-retrieval", "reference-workload"),
            new("REFERENCE_SEARCH", null, "reference-authorization", "reference-customer")));
        services.AddSingleton<ISearchProjectionSource>(source);
        services.AddSingleton<IDerivedSearchIndex>(index);
        services.AddSingleton<ISearchAuthorizationPolicy>(authorization);
        services.AddTransient<SearchRetrievalApplication>();
        if (TenantBoundaryOptions.Selected(configuration)) services.AddSingleton<ISearchAuthorizationPolicy, TenantSearchRetrievalAuthorizationPolicy>();
        return services;
    }
}
