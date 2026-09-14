using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.Audit.Application;
using Monergy.Services.Audit.Infrastructure;

namespace Monergy.Services.Audit;

public static class AuditRegistration
{
    public static IServiceCollection AddAuditReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditEvidenceRepository, AppendOnlyInMemoryAuditRepository>();
        services.AddSingleton<AuditApplication>();
        return services;
    }
}
