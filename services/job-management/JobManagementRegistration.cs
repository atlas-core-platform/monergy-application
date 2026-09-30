using Microsoft.Extensions.DependencyInjection;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;

namespace Monergy.Services.JobManagement;

public static class JobManagementRegistration
{
    private static IServiceCollection AddWorkerInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<EventDeliveryTelemetry>();
        services.AddSingleton<IEventDeliveryTelemetry>(provider => provider.GetRequiredService<EventDeliveryTelemetry>());
        services.AddSingleton<IGovernedEventTransport<AuditableEvent>>(provider =>
            new ReferenceGovernedEventTransport<AuditableEvent>(configuration,
                provider.GetServices<IGovernedEventConsumer<AuditableEvent>>(),
                provider.GetRequiredService<IEventDeliveryTelemetry>()));
        services.AddSingleton<ReferenceJobExecutionTargetRegistry>();
        services.AddSingleton<IJobExecutionTargetResolver>(provider =>
            provider.GetRequiredService<ReferenceJobExecutionTargetRegistry>());
        services.AddSingleton<JobOutboxDispatcher>();
        return services;
    }

    public static IServiceCollection AddJobManagementReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IJobRepository, InMemoryJobRepository>();
        services.AddSingleton<ReferenceJobExecutionPolicy>();
        services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        services.AddSingleton<JobManagementApplication>();
        return services.AddWorkerInfrastructure(configuration);
    }

    public static IServiceCollection AddJobManagementPhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IJobRepository, PostgresJobRepository>();
        services.AddSingleton<ReferenceJobExecutionPolicy>();
        services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        services.AddSingleton<JobManagementApplication>();
        return services.AddWorkerInfrastructure(configuration);
    }
}
