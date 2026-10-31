using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;

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
        return services;
    }
}
