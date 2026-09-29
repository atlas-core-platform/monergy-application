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
        authorization.Grant(new(new("reference-actor", "HUMAN", DateTimeOffset.UnixEpoch, "reference-authentication"),
            new("reporting", "reference-reporting-workload"),
            new("REFERENCE_REPORTING", null, "reference-reporting-authorization", "reference-customer")));
        services.AddSingleton<IReportingAuthorizationPolicy>(authorization);
        services.AddSingleton<IReportSourceReader>(new ReferenceReportSourceReader(configuration));
        services.AddSingleton<IReportRepository, PostgresReportRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<ReportingApplication>();
        return services;
    }
}
