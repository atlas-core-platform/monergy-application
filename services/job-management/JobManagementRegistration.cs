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
        if (JobAuditDeliveryOptions.Selected(configuration))
        {
            services.AddHttpClient<HttpJobAuditEventConsumer>(client =>
            {
                client.BaseAddress = JobAuditDeliveryOptions.Endpoint(configuration);
                client.Timeout = TimeSpan.FromSeconds(5);
            });
            services.AddTransient<IGovernedEventConsumer<AuditableEvent>>(provider =>
                provider.GetRequiredService<HttpJobAuditEventConsumer>());
        }
        return services;
    }

    public static IServiceCollection AddJobManagementReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryJobRepository>();
        services.AddSingleton<IJobRepository>(provider=>provider.GetRequiredService<InMemoryJobRepository>());
        services.AddSingleton<IJobAdministrationRepository>(provider=>provider.GetRequiredService<InMemoryJobRepository>());
        var documentProcessing = configuration.GetValue<bool>("Monergy:DocumentProcessing:Enabled");
        var reportGeneration = configuration.GetValue<bool>("Monergy:ReportGeneration:Enabled");
        if (documentProcessing || reportGeneration)
        {
            services.AddSingleton<TenantAccessClient>();
            services.AddSingleton<OwnerBackedJobExecutionPolicy>();
            services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<OwnerBackedJobExecutionPolicy>());
            services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<OwnerBackedJobExecutionPolicy>());
        }
        else
        {
            services.AddSingleton<ReferenceJobExecutionPolicy>();
            services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
            services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        }
        if (documentProcessing)
        {
            services.AddSingleton<DocumentProcessingJobRegistry>();
            services.AddSingleton<HttpDocumentProcessingTarget>();
            services.AddSingleton<IJobExecutionTarget>(provider => provider.GetRequiredService<HttpDocumentProcessingTarget>());
        }
        if (reportGeneration)
        {
            services.AddSingleton<HttpReportGenerationTarget>();
            services.AddSingleton<IJobExecutionTarget>(provider => provider.GetRequiredService<HttpReportGenerationTarget>());
        }
        services.AddSingleton<JobManagementApplication>();
        return services.AddWorkerInfrastructure(configuration);
    }

    public static IServiceCollection AddJobManagementPhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        var reportGeneration = configuration.GetValue<bool>("Monergy:ReportGeneration:Enabled");
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PostgresJobRepository>();
        services.AddSingleton<IJobRepository>(provider=>provider.GetRequiredService<PostgresJobRepository>());
        services.AddSingleton<IJobAdministrationRepository>(provider=>provider.GetRequiredService<PostgresJobRepository>());
        if (reportGeneration)
        {
            services.AddSingleton<TenantAccessClient>();
            services.AddSingleton<OwnerBackedJobExecutionPolicy>();
            services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<OwnerBackedJobExecutionPolicy>());
            services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<OwnerBackedJobExecutionPolicy>());
            services.AddSingleton<HttpReportGenerationTarget>();
            services.AddSingleton<IJobExecutionTarget>(provider => provider.GetRequiredService<HttpReportGenerationTarget>());
        }
        else
        {
            services.AddSingleton<ReferenceJobExecutionPolicy>();
            services.AddSingleton<IJobAuthorizationPolicy>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
            services.AddSingleton<IJobConsentDecisionPort>(provider => provider.GetRequiredService<ReferenceJobExecutionPolicy>());
        }
        services.AddSingleton<JobManagementApplication>();
        return services.AddWorkerInfrastructure(configuration);
    }
}
