using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;

namespace Monergy.Services.FinancialRules;

public static class FinancialRulesRegistration
{
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
        services.AddSingleton<ReferenceCalculationAccessPolicy>();
        services.AddSingleton<ICalculationAccessPolicy>(provider => provider.GetRequiredService<ReferenceCalculationAccessPolicy>());
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
}
