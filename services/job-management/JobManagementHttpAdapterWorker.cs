using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;
using Monergy.Services.JobManagement.Infrastructure;

namespace Monergy.Services.JobManagement;

/// <summary>
/// Secondary HTTP adapter for health and bounded LOCAL/UAT workload control.
/// The outer Worker host remains the authoritative owner of all business state.
/// </summary>
public sealed class JobManagementHttpAdapterWorker(
    IServiceProvider ownerServices,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var documentProcessing =
            ReferenceAdapterGuard.IsSelected(configuration) &&
            configuration.GetValue<bool>("Monergy:DocumentProcessing:Enabled");

        if (documentProcessing)
        {
            builder.Services.AddSingleton(ownerServices.GetRequiredService<JobManagementApplication>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<DocumentProcessingJobRegistry>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<IJobRepository>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<IJobAuthorizationPolicy>());
            builder.Services.AddSingleton(ownerServices.GetRequiredService<HttpDocumentProcessingTarget>());
        }

        await using var app = builder.Build();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", primaryHost = "worker" }));
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", primaryHost = "worker" }));
        if (documentProcessing)
            app.MapDocumentProcessingJobs(configuration);

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
