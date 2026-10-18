using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;
using Monergy.Services.DocumentIntelligence.Infrastructure;

namespace Monergy.Services.DocumentIntelligence;

public static class DocumentIntelligenceRegistration
{
    public static IServiceCollection AddDocumentIntelligenceReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryDocumentProcessingRepository>();
        services.AddSingleton<IDocumentProcessingRepository>(provider => provider.GetRequiredService<InMemoryDocumentProcessingRepository>());
        services.AddSingleton<IImageTextRecognizer, TesseractImageTextRecognizer>();
        services.AddSingleton<SalarySlipPngExtractor>();
        services.AddSingleton<IDocumentExtractor, StructuredDocumentExtractor>();
        services.AddSingleton<TenantAccessClient>();
        services.AddSingleton<IEvidenceContentReader, HttpEvidenceContentReader>();
        services.AddSingleton<IExecutionPolicy, OwnerBackedExecutionPolicy>();
        services.AddSingleton<DocumentProcessingApplication>();
        return services;
    }
}
