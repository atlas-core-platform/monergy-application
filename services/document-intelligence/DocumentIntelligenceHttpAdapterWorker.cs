using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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
        var tenantBoundary = TenantBoundaryOptions.Selected(configuration);
        var adapterUrl = configuration["urls"];
        if (string.IsNullOrWhiteSpace(adapterUrl) && tenantBoundary)
            adapterUrl = ownerServices.GetRequiredService<TenantBoundaryOptions>().ProbeUrl.ToString();
        var endpoint = TenantAccessIntegration.LoopbackRoot(
            adapterUrl ?? throw new InvalidOperationException("Worker HTTP adapter requires an explicit loopback URL."));
        builder.WebHost.UseUrls(endpoint.ToString());

        if (tenantBoundary)
        {
            builder.Services.AddSingleton(ownerServices.GetRequiredService<TenantBoundaryOptions>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<TenantAccessClient>());
        }
        var documentProcessing = ReferenceAdapterGuard.IsSelected(configuration);
        if (documentProcessing)
        {
            builder.Services.AddSingleton(ownerServices.GetRequiredService<DocumentProcessingApplication>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<IDocumentProcessingRepository>());
        }

        await using var app = builder.Build();
        if (tenantBoundary)
            app.UseMiddleware<TenantBoundaryMiddleware>();
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
