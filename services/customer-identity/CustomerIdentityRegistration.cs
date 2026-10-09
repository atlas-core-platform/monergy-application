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
        services.AddSingleton<ITenantCustomerResourceDirectory, ReferenceTenantCustomerResourceDirectory>();
        services.AddSingleton<ICustomerIdentityRepository>(provider =>
            provider.GetRequiredService<InMemoryCustomerIdentityRepository>());
        services.AddSingleton<InMemoryCustomerIdentityTelemetry>();
        services.AddSingleton<ICustomerIdentityTelemetry>(provider =>
            provider.GetRequiredService<InMemoryCustomerIdentityTelemetry>());
        services.AddSingleton(TrustedSessionPolicy.ReferenceDefault);
        services.AddSingleton<InMemoryTrustedSessionRepository>();
        services.AddSingleton<ITrustedSessionRepository>(provider =>
            provider.GetRequiredService<InMemoryTrustedSessionRepository>());
        services.AddSingleton<InMemoryCustomerIdentityEventSink>();
        services.AddSingleton<ICustomerIdentityEventSink>(provider =>
            provider.GetRequiredService<InMemoryCustomerIdentityEventSink>());
        services.AddSingleton<TrustedSessionLifecycle>();
        services.AddSingleton<CustomerIdentityApplication>();
        return services;
    }
}
