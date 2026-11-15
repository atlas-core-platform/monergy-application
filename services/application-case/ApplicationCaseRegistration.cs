using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;
using Monergy.Services.ApplicationCase.Infrastructure;

namespace Monergy.Services.ApplicationCase;

public static class ApplicationCaseRegistration
{
    public static IServiceCollection AddApplicationCaseReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CustomerOwnerClient>();
        services.AddSingleton<TenantAccessClient>();
        services.AddSingleton<ApplicationCaseAuthority>();
        services.AddSingleton<AssistanceCaseApplication>();
        services.AddSingleton<IAssistanceCaseApplication>(provider =>
            provider.GetRequiredService<AssistanceCaseApplication>());
        services.AddSingleton<AdvisorEngagementApplication>();
        services.AddSingleton<IAdvisorEngagementApplication>(provider =>
            provider.GetRequiredService<AdvisorEngagementApplication>());
        services.AddSingleton<LenderApplication>();
        services.AddSingleton<ILenderApplication>(provider =>
            provider.GetRequiredService<LenderApplication>());
        return services;
    }

    public static IServiceCollection AddApplicationCasePhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CustomerOwnerClient>();
        services.AddSingleton<TenantAccessClient>();
        services.AddSingleton<ApplicationCaseAuthority>();
        services.AddSingleton<PostgresAssistanceCaseApplication>();
        services.AddSingleton<IAssistanceCaseApplication>(provider =>
            provider.GetRequiredService<PostgresAssistanceCaseApplication>());
        services.AddSingleton<PostgresAdvisorEngagementApplication>();
        services.AddSingleton<IAdvisorEngagementApplication>(provider =>
            provider.GetRequiredService<PostgresAdvisorEngagementApplication>());
        services.AddSingleton<PostgresLenderApplication>();
        services.AddSingleton<ILenderApplication>(provider =>
            provider.GetRequiredService<PostgresLenderApplication>());
        services.AddHealthChecks().AddCheck<PostgresAssistanceCaseApplication>("application-case-storage");
        return services;
    }
}
