using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
using Monergy.Services.CustomerIdentity.Infrastructure;

namespace Monergy.Services.CustomerIdentity;

public static class CustomerIdentityRegistration
{
    public static IServiceCollection AddCustomerIdentityReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReferenceCustomerAuthenticationProvider>();
        services.AddSingleton<ICustomerAuthenticationProvider>(provider =>
            provider.GetRequiredService<ReferenceCustomerAuthenticationProvider>());
        services.AddSingleton<InMemoryCustomerIdentityRepository>();
        services.AddSingleton<ICustomerIdentityRepository>(provider =>
            provider.GetRequiredService<InMemoryCustomerIdentityRepository>());
        services.AddSingleton<InMemoryCustomerIdentityTelemetry>();
        services.AddSingleton<ICustomerIdentityTelemetry>(provider =>
            provider.GetRequiredService<InMemoryCustomerIdentityTelemetry>());
        services.AddSingleton<CustomerIdentityApplication>();
        return services;
    }
}
