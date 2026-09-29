using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;

namespace Monergy.Services.FinancialProfile;

public static class FinancialProfileRegistration
{
    public static IServiceCollection AddFinancialProfileReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryFinancialProfileRepository>();
        services.AddSingleton<IFinancialProfileRepository>(provider => provider.GetRequiredService<InMemoryFinancialProfileRepository>());
        services.AddSingleton<FinancialProfileApplication>();
        return services;
    }

    public static IServiceCollection AddFinancialProfilePhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFinancialProfileRepository, PostgresFinancialProfileRepository>();
        services.AddSingleton<FinancialProfileApplication>();
        return services;
    }
}
