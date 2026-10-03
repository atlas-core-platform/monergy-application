using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.Reporting.Application;
using Monergy.Services.Reporting.Infrastructure;

namespace Monergy.Services.Reporting;

public static class ReportingRegistration
{
    public static IServiceCollection AddReportingReferenceAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        var authorization = new ReferenceReportingAuthorizationPolicy(configuration);
        authorization.Grant(new(new("reference-actor", "HUMAN", DateTimeOffset.UnixEpoch, "reference-authentication"),
            new("reporting", "reference-reporting-workload"),
            new("REFERENCE_REPORTING", null, "reference-reporting-authorization", "reference-customer")));
        services.AddSingleton<IReportingAuthorizationPolicy>(authorization);
        services.AddSingleton<IReportSourceReader>(new ReferenceReportSourceReader(configuration));
        services.AddSingleton<IReportRepository>(new InMemoryReportRepository(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<ReportingApplication>();
        return services;
    }

    public static IServiceCollection AddReportingPhysicalPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        var authorization = new ReferenceReportingAuthorizationPolicy(configuration);
        var persistedReporting = string.Equals(configuration["Monergy:D11:Profile"], "persisted-reporting",
            StringComparison.Ordinal);
        if (persistedReporting)
        {
            foreach (var context in D11LocalSecurityContexts.BrowserContexts(configuration)) authorization.Grant(context);
        }
        else
        {
            authorization.Grant(new(new("reference-actor", "HUMAN", DateTimeOffset.UnixEpoch, "reference-authentication"),
                new("reporting", "reference-reporting-workload"),
                new("REFERENCE_REPORTING", null, "reference-reporting-authorization", "reference-customer")));
        }
        services.AddSingleton<IReportingAuthorizationPolicy>(authorization);
        if (persistedReporting)
        {
            AddSourceClient(services, ServiceContractReportSourceReader.EvidenceClient,
                PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:EvidenceBaseAddress"));
            AddSourceClient(services, ServiceContractReportSourceReader.FinancialProfileClient,
                PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:FinancialProfileBaseAddress"));
            AddSourceClient(services, ServiceContractReportSourceReader.FinancialRulesClient,
                PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:FinancialRulesBaseAddress"));
            services.AddSingleton<IReportSourceReader, ServiceContractReportSourceReader>();
            services.AddSingleton<IEventDeliveryTelemetry, EventDeliveryTelemetry>();
            services.AddHttpClient<IGovernedEventTransport<AuditableEvent>, LocalHttpAuditEventTransport>(client =>
            {
                client.BaseAddress = new Uri(PhysicalPersistenceGuard.Require(configuration,
                    "Monergy:D11:AuditBaseAddress"), UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(10);
            });
            services.AddSingleton<ReportingAuditOutboxDispatcher>();
            services.AddSingleton<ReportingOutboxHostedService>();
            services.AddSingleton<IReportingDispatchStatus>(provider =>
                provider.GetRequiredService<ReportingOutboxHostedService>());
            services.AddHostedService(provider => provider.GetRequiredService<ReportingOutboxHostedService>());
        }
        else
        {
            services.AddSingleton<IReportSourceReader>(new ReferenceReportSourceReader(configuration));
        }
        services.AddSingleton<IReportRepository, PostgresReportRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<ReportingApplication>();
        return services;
    }

    private static void AddSourceClient(IServiceCollection services, string name, string address) =>
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(address, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
}
