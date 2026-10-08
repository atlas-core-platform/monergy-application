using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Monergy.Platform;

public static class MonergyPlatformExtensions
{
    public static TBuilder AddMonergyPlatform<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (TenantBoundaryOptions.Selected(builder.Configuration))
        {
            builder.Services.AddSingleton(TenantBoundaryOptions.Read(builder.Configuration, builder.Environment, serviceName));
            builder.Services.AddSingleton(provider => new TenantAccessClient(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
            builder.Services.AddHttpContextAccessor();
            if (builder is WebApplicationBuilder) builder.Services.AddTransient<IStartupFilter, TenantBoundaryStartupFilter>();
            else builder.Services.AddHostedService<TenantBoundaryWorkerHost>();
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        builder.Services
            .AddSingleton<ILifecycleTelemetry, OpenTelemetryLifecycleTelemetry>()
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter())
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter());

        return builder;
    }
}
