using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monergy.Services.FinancialRules;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Infrastructure;
using Xunit;

namespace Monergy.Persistence.Tests;

public sealed class FinancialRulesPersistenceCompositionTests
{
    [Fact]
    public void PhysicalCompositionSelectsEveryPostgresReceiptRepository()
    {
        var services = new ServiceCollection();
        services.AddFinancialRulesPhysicalPersistence(Configuration(includeConnection: true));

        AssertImplementation<ICalculationRepository, PostgresCalculationRepository>(services);
        AssertImplementation<INetWorthCalculationRepository, PostgresNetWorthCalculations>(services);
        AssertImplementation<IMonthlyCashflowCalculationRepository, PostgresMonthlyCashflowCalculations>(services);
        AssertImplementation<IDebtServiceRatioCalculationRepository, PostgresDebtServiceRatioCalculations>(services);
        AssertImplementation<ILiquidityRatioCalculationRepository, PostgresLiquidityRatioCalculations>(services);
        AssertImplementation<IDebtToIncomeCalculationRepository, PostgresDebtToIncomeCalculations>(services);
        AssertImplementation<IAssetAllocationCalculationRepository, PostgresAssetAllocationCalculations>(services);
        AssertImplementation<IMonthlyNetCashflowRateCalculationRepository, PostgresMonthlyNetCashflowRateCalculations>(services);
        AssertImplementation<IMonthlyEmiShareCalculationRepository, PostgresMonthlyEmiShareCalculations>(services);
        AssertImplementation<IMonthlyDebtServiceCoverageCalculationRepository, PostgresMonthlyDebtServiceCoverageCalculations>(services);
    }

    [Fact]
    public void PhysicalCompositionFailsFastWithoutRuntimeConnection()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddFinancialRulesPhysicalPersistence(Configuration(includeConnection: false)));
        Assert.Contains("Monergy:Persistence:FinancialRules:RuntimeConnection", error.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(bool includeConnection)
    {
        var values = new Dictionary<string, string?>
        {
            ["Monergy:ExecutionZone"] = "CI_EPHEMERAL",
            ["Monergy:ReferenceAdapters"] = "false",
            ["Monergy:Persistence:Provider"] = "POSTGRESQL_S3",
            ["Monergy:NetWorth:Enabled"] = "true",
            ["Monergy:NetWorth:FinancialProfileUrl"] = "http://127.0.0.1:5113/",
            ["Monergy:MonthlyCashflow:Enabled"] = "true",
            ["Monergy:MonthlyCashflow:FinancialProfileUrl"] = "http://127.0.0.1:5113/",
        };
        if (includeConnection)
            values["Monergy:Persistence:FinancialRules:RuntimeConnection"] = "Host=127.0.0.1;Database=fixture;Username=fixture;Password=fixture";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static void AssertImplementation<TService, TImplementation>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(TService));
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
    }
}
