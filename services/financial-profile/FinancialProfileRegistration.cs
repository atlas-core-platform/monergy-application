using Microsoft.Extensions.DependencyInjection;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;

namespace Monergy.Services.FinancialProfile;

public static class FinancialProfileRegistration
{
    public static IEndpointRouteBuilder MapD11LocalShutdown(this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        var token = PhysicalPersistenceGuard.Require(configuration, "Monergy:D11:ControllerToken");
        endpoints.MapPost("/operations/local/control/stop", (HttpRequest request,
            IHostApplicationLifetime lifetime) =>
        {
            if (!LocalControlToken.IsValid(request.Headers["X-Monergy-Local-Control"].ToString(), token))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });
        return endpoints;
    }

    public static IServiceCollection AddFinancialProfileReferenceAdapters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryFinancialProfileRepository>();
        services.AddSingleton<IFinancialProfileRepository>(provider => provider.GetRequiredService<InMemoryFinancialProfileRepository>());
        services.AddSingleton<FinancialProfileApplication>();
        return services;
    }

    public static IServiceCollection AddFinancialProfilePhysicalPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFinancialProfileRepository, PostgresFinancialProfileRepository>();
        services.AddSingleton<FinancialProfileApplication>();
        return services;
    }
}
