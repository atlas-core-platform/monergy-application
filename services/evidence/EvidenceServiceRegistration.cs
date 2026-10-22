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
        services.AddSingleton<IDocumentMonitoringRepository>(provider=>provider.GetRequiredService<InMemoryEvidenceRepository>());
        services.AddSingleton<IDocumentVaultRepository>(provider=>provider.GetRequiredService<InMemoryEvidenceRepository>());
        services.AddSingleton(_ =>
        {
            var store = new ReferenceEvidenceContentStore();
            if (configuration["Monergy:OnboardingFixtures"] == "true")
            {
                const string content = "Monergy local onboarding acceptance fixture. Not customer financial data.";
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
                store.Seed("reference://onboarding-evidence", hash, "text/plain", content);
            }
            return store;
        });
        services.AddSingleton<IEvidenceContentStore>(provider => provider.GetRequiredService<ReferenceEvidenceContentStore>());
        services.AddSingleton<IEvidenceIntakeStore>(provider => provider.GetRequiredService<ReferenceEvidenceContentStore>());
        services.AddSingleton<ClamdEvidenceMalwareScanner>();
        services.AddSingleton<IEvidenceMalwareScanner>(provider => provider.GetRequiredService<ClamdEvidenceMalwareScanner>());
        services.AddSingleton<EvidenceApplication>();
        return services;
    }

    public static IServiceCollection AddEvidencePhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        if (configuration.GetValue<bool>("Monergy:ProductIntake:Enabled"))
        {
            if (!TenantBoundaryOptions.Selected(configuration))
                throw new InvalidOperationException("Durable product Evidence requires the current tenant access boundary.");
            services.AddHttpContextAccessor();
            services.AddSingleton<TenantEvidenceRepository>();
            services.AddSingleton<IEvidenceRepository>(p => p.GetRequiredService<TenantEvidenceRepository>());
            services.AddSingleton<IDocumentMonitoringRepository>(p => p.GetRequiredService<TenantEvidenceRepository>());
            services.AddSingleton<IDocumentVaultRepository>(p => p.GetRequiredService<TenantEvidenceRepository>());
            services.AddSingleton<ClamdEvidenceMalwareScanner>();
            services.AddSingleton<IEvidenceMalwareScanner>(p => p.GetRequiredService<ClamdEvidenceMalwareScanner>());
        }
        else
        {
            services.AddSingleton<PostgresEvidenceRepository>();
            services.AddSingleton<IEvidenceRepository>(p => p.GetRequiredService<PostgresEvidenceRepository>());
            services.AddSingleton<IDocumentMonitoringRepository>(p => p.GetRequiredService<PostgresEvidenceRepository>());
            services.AddSingleton<IDocumentVaultRepository>(p => p.GetRequiredService<PostgresEvidenceRepository>());
        }
        services.AddSingleton<S3EvidenceContentStore>();
        services.AddSingleton<IEvidenceContentStore>(provider => provider.GetRequiredService<S3EvidenceContentStore>());
        services.AddSingleton<IEvidenceIntakeStore>(provider => provider.GetRequiredService<S3EvidenceContentStore>());
        services.AddSingleton<EvidenceApplication>();
        return services;
    }
}
