using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;

public sealed class MonthlyNetCashflowRatePersistenceTests:PhysicalPostgresFixture
{
    [Fact]
    public async Task RateReceiptSurvivesRestartIsTenantBoundIdempotentAndImmutable()
    {
        var rules=Configuration("FinancialRules");var customer="cashflow-rate-"+Guid.NewGuid().ToString("N");var day=new DateOnly(2026,9,30);var income=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary","1000.00","INR",day,"Customer reviewed");var expense=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","HOUSEHOLD","Household","750.00","INR",day,"Customer reviewed");var lines=new[]{income,expense};var position=new ReviewedMonthlyCashflowPosition("position-"+customer,Guid.NewGuid().ToString("N"),customer,new(day,"INR",lines,true,true,Household:"Customer household"),lines.Select(x=>new MonthlyCashflowPositionLine(x,null)).ToArray(),"actor-rate",DateTimeOffset.UtcNow);var source=new MonthlyCashflowCalculation("cashflow-"+customer,Guid.NewGuid().ToString("N"),customer,position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED","1000.00","750.00","250.00",Array.Empty<string>(),"actor-rate",DateTimeOffset.UtcNow);
        var operation=Guid.NewGuid().ToString("N");var value=new MonthlyNetCashflowRateCalculation("cashflow-rate-"+Guid.NewGuid().ToString("N"),operation,customer,source,MonthlyNetCashflowRateDefinition.Id,MonthlyNetCashflowRateDefinition.Version,MonthlyNetCashflowRateDefinition.DefinitionHash,MonthlyNetCashflowRateDefinition.Semantics,"CALCULATED","1000.00","750.00","250.00","25.00",Array.Empty<string>(),"actor-rate",DateTimeOffset.UtcNow);const string fingerprint="fingerprint";
        await using(var repository=new PostgresMonthlyNetCashflowRateCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-rate",fingerprint,value,default)));Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-rate",fingerprint,value,default)));}
        await using(var repository=new PostgresMonthlyNetCashflowRateCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.ReadAsync("T001",customer,value.CalculationId,default)));Assert.Single((await repository.ListAsync("T001",customer,null,default)).Items);Assert.Null(await repository.ReadAsync("T002",customer,value.CalculationId,default));Assert.Null(await repository.ReadAsync("T001","foreign",value.CalculationId,default));Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repository.CreateAsync("T001",customer,"actor-rate","changed",value,default))).Status);}
        await using var sql=new NpgsqlConnection(Runtime("financial-rules"));var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_rules.monthly_net_cashflow_rate_results SET payload=payload WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",update.SqlState);var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_rules.monthly_net_cashflow_rate_results WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",delete.SqlState);
    }
}
