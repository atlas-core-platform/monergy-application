using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.Evidence.Application;
using Monergy.Services.Evidence.Infrastructure;

namespace Monergy.Services.Evidence;

public static class EvidenceServiceRegistration
{
    public static IServiceCollection AddEvidenceReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryEvidenceRepository>();
        services.AddSingleton<IEvidenceRepository>(provider => provider.GetRequiredService<InMemoryEvidenceRepository>());
        services.AddSingleton<ReferenceEvidenceContentStore>();
        services.AddSingleton<IEvidenceContentStore>(provider => provider.GetRequiredService<ReferenceEvidenceContentStore>());
        services.AddSingleton<EvidenceApplication>();
        return services;
    }
}
