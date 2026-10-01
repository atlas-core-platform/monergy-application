using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;

namespace Monergy.Services.FinancialRules;

public static class FinancialRulesRegistration
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

    public static IServiceCollection AddFinancialRulesReferenceAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        ReferenceAdapterGuard.EnsureAllowed(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReferenceRuleRegistry>(_ =>
        {
            var registry = new ReferenceRuleRegistry(configuration);
            registry.Register(new EngineeringRule("engineering.sum", "1.0.0"));
            registry.Register(new EngineeringRule("engineering.sum", "2.0.0"));
            registry.Register(new EngineeringRule("engineering.ratio", "1.0.0"));
            return registry;
        });
        services.AddSingleton<IRuleRegistry>(provider => provider.GetRequiredService<ReferenceRuleRegistry>());
        AddAccessPolicy(services, configuration);
        services.AddSingleton<InMemoryCalculationRepository>();
        services.AddSingleton<ICalculationRepository>(provider => provider.GetRequiredService<InMemoryCalculationRepository>());
        services.AddHttpClient<IFinancialInputReader, FinancialProfileContractClient>(client =>
        {
            var endpoint = configuration["Monergy:FinancialProfileBaseAddress"];
            if (!string.IsNullOrWhiteSpace(endpoint)) { client.BaseAddress = new Uri(endpoint, UriKind.Absolute); }
            client.Timeout = TimeSpan.FromSeconds(10); // LOCAL/CI engineering timeout; not a client target.
        });
        services.AddTransient<FinancialRulesApplication>();
        return services;
    }

    public static IServiceCollection AddFinancialRulesPhysicalPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        PhysicalPersistenceGuard.EnsureAllowed(configuration);
        AddCommon(services, configuration);
        services.AddSingleton<ICalculationRepository, PostgresCalculationRepository>();
        return services;
    }

    private static void AddCommon(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReferenceRuleRegistry>(_ =>
        {
            var registry = new ReferenceRuleRegistry(configuration);
            registry.Register(new EngineeringRule("engineering.sum", "1.0.0"));
            registry.Register(new EngineeringRule("engineering.sum", "2.0.0"));
            registry.Register(new EngineeringRule("engineering.ratio", "1.0.0"));
            return registry;
        });
        services.AddSingleton<IRuleRegistry>(provider => provider.GetRequiredService<ReferenceRuleRegistry>());
        AddAccessPolicy(services, configuration);
        services.AddHttpClient<IFinancialInputReader, FinancialProfileContractClient>(client =>
        {
            var endpoint = configuration["Monergy:FinancialProfileBaseAddress"];
            if (!string.IsNullOrWhiteSpace(endpoint)) client.BaseAddress = new Uri(endpoint, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddTransient<FinancialRulesApplication>();
    }

    private static void AddAccessPolicy(IServiceCollection services, IConfiguration configuration)
    {
        var policy = new ReferenceCalculationAccessPolicy(configuration, TimeProvider.System);
        if (string.Equals(configuration["Monergy:D11:Profile"], "persisted-reporting", StringComparison.Ordinal))
        {
            var customers = (configuration["Monergy:D11:SyntheticCustomers"] ?? "d11-customer-a,d11-customer-b")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
            foreach (var customer in customers)
            {
                foreach (var workload in new[]
                {
                    (Id: "reporting", Identity: configuration["Monergy:D11:ReportingWorkloadIdentityId"] ?? "d11-reporting-workload"),
                    (Id: "local-fixture-seeder", Identity: "d11-local-fixture-seeder"),
                })
                {
                    var context = new Monergy.Contracts.TrustedSecurityContext(
                        new($"d11-synthetic-actor-{customer}", "SYNTHETIC_HUMAN", DateTimeOffset.UnixEpoch,
                            $"d11-authentication-{customer}"),
                        new(workload.Id, workload.Identity),
                        new("D11_PERSISTED_REPORTING", $"d11-consent-{customer}",
                            $"d11-{workload.Id}-{customer}-authorization", customer));
                    policy.SetGrant(new(context, DateTimeOffset.MaxValue, false, true,
                        DateTimeOffset.MaxValue, false));
                }
            }
        }
        services.AddSingleton(policy);
        services.AddSingleton<ICalculationAccessPolicy>(policy);
    }
}
