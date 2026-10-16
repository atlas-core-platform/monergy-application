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
        var authorization = new ReferenceSearchAuthorizationPolicy(configuration);
        authorization.Grant(new(
            new("reference-actor", "HUMAN", DateTimeOffset.UnixEpoch, "reference-authentication"),
            new("search-retrieval", "reference-workload"),
            new("REFERENCE_SEARCH", null, "reference-authorization", "reference-customer")));
        services.AddSingleton<ISearchAuthorizationPolicy>(authorization);

        if (ProductSearchProjectionSource.Selected(configuration))
        {
            services.AddHttpContextAccessor();
            AddSourceClient(
                services,
                ProductSearchProjectionSource.EvidenceClient,
                TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:EvidenceUrl"));
            AddSourceClient(
                services,
                ProductSearchProjectionSource.FinancialProfileClient,
                TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:FinancialProfileUrl"));
            services.AddSingleton<ProductSearchProjectionSource>();
            services.AddSingleton<ISearchProjectionSource>(provider =>
                provider.GetRequiredService<ProductSearchProjectionSource>());
            services.AddSingleton<IDerivedSearchIndex>(_ =>
            {
                var index = new InMemoryDerivedSearchIndex(configuration);
                index.Rebuild([]);
                return index;
            });
        }
        else
        {
            var source = new ReferenceSearchProjectionSource(configuration);
            var index = new InMemoryDerivedSearchIndex(configuration);
            index.Rebuild(source.ReadAll());
            services.AddSingleton<ISearchProjectionSource>(source);
            services.AddSingleton<IDerivedSearchIndex>(index);
        }

        services.AddTransient<SearchRetrievalApplication>();
        if (TenantBoundaryOptions.Selected(configuration))
            services.AddSingleton<ISearchAuthorizationPolicy, TenantSearchRetrievalAuthorizationPolicy>();
        return services;
    }

    private static void AddSourceClient(IServiceCollection services, string name, string address) =>
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(address, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
}
