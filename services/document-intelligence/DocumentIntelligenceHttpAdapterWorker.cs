using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence;

/// <summary>
/// Secondary HTTP adapter for health and bounded LOCAL/UAT workload control.
/// The outer Worker host remains the authoritative owner of document-processing state.
/// </summary>
public sealed class DocumentIntelligenceHttpAdapterWorker(
    IServiceProvider ownerServices,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var documentProcessing = ReferenceAdapterGuard.IsSelected(configuration);
        if (documentProcessing)
        {
            builder.Services.AddSingleton(ownerServices.GetRequiredService<DocumentProcessingApplication>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<IDocumentProcessingRepository>());
        }

        await using var app = builder.Build();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", primaryHost = "worker" }));
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", primaryHost = "worker" }));
        if (documentProcessing)
            app.MapDocumentProcessing(configuration);

        await app.StartAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
