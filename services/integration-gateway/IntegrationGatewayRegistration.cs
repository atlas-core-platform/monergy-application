using Monergy.Platform;
using Monergy.Services.IntegrationGateway.Application;
using Monergy.Services.IntegrationGateway.Infrastructure;

namespace Monergy.Services.IntegrationGateway;

public static class IntegrationGatewayRegistration
{
    public static IServiceCollection AddIntegrationGatewayReferenceAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new GatewayExecutionOptions());
        services.AddSingleton<ReferenceConnectorRegistry>(_ =>
        {
            var registry = new ReferenceConnectorRegistry(configuration);
            registry.Register(new ReferenceProviderConnector("reference-source-facts",
                ["READ_CANONICAL_SOURCE_FACTS"], configuration));
            return registry;
        });
        services.AddSingleton<IConnectorRegistry>(provider => provider.GetRequiredService<ReferenceConnectorRegistry>());
        services.AddSingleton<ReferenceGatewayAuthorizationPolicy>();
        services.AddSingleton<IGatewayAuthorizationPolicy>(provider => provider.GetRequiredService<ReferenceGatewayAuthorizationPolicy>());
        services.AddSingleton<ReferenceConsentDecisionPort>();
        services.AddSingleton<IConsentDecisionPort>(provider => provider.GetRequiredService<ReferenceConsentDecisionPort>());
        services.AddSingleton<InMemoryGatewayOperationRepository>();
        services.AddSingleton<IGatewayOperationRepository>(provider => provider.GetRequiredService<InMemoryGatewayOperationRepository>());
        services.AddSingleton<InMemoryGatewayEventSink>();
        services.AddSingleton<IGatewayEventSink>(provider => provider.GetRequiredService<InMemoryGatewayEventSink>());
        services.AddSingleton<InMemoryGatewayTelemetry>();
        services.AddSingleton<IGatewayTelemetry>(provider => provider.GetRequiredService<InMemoryGatewayTelemetry>());
        services.AddSingleton<IConnectorRuntimeState, ConnectorRuntimeState>();
        services.AddTransient<IntegrationGatewayApplication>();
        return services;
    }
}
