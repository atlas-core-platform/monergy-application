using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;

namespace Monergy.Services.DocumentIntelligence;

public static class DocumentIntelligenceRegistration
{
    public static IServiceCollection AddDocumentIntelligenceReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration,
        IEvidenceContentReader contentReader)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryDocumentProcessingRepository>();
        services.AddSingleton<IDocumentProcessingRepository>(provider => provider.GetRequiredService<InMemoryDocumentProcessingRepository>());
        services.AddSingleton<IDocumentExtractor, FixtureDocumentExtractor>();
        services.AddSingleton<IExecutionPolicy, ReferenceExecutionPolicy>();
        services.AddSingleton(contentReader);
        services.AddSingleton<DocumentProcessingApplication>();
        return services;
    }
}
