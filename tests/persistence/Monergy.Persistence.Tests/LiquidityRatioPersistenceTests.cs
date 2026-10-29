using Dapper;
using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;
public sealed partial class PersistentFoundationTests
{
    [Fact]
    public async Task LiquidityRatioReceiptSurvivesRestartIsTenantBoundAndCannotBeRewritten()
    {
        var rules=Configuration("FinancialRules");var customer="liquidity-"+Guid.NewGuid().ToString("N");var asset=new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","CASH","Cash","1200.00","INR",new(2026,10,1),"Reviewed cash");var netPosition=new ReviewedNetWorthPosition("net-position-"+customer,Guid.NewGuid().ToString("N"),customer,new(new(2026,10,1),"INR",new[]{asset},true,true),new[]{new NetWorthPositionLine(asset,null)},"actor-liquidity",DateTimeOffset.UtcNow);var net=new NetWorthCalculation("networth-"+customer,Guid.NewGuid().ToString("N"),customer,netPosition,NetWorthDefinition.Id,NetWorthDefinition.Version,NetWorthDefinition.DefinitionHash,NetWorthDefinition.Semantics,"CALCULATED","1200.00","0.00","1200.00",Array.Empty<string>(),"actor-liquidity",DateTimeOffset.UtcNow);
        var expense=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","HOUSEHOLD","Expenses","600.00","INR",new(2026,9,30),"Reviewed expenses");var cashPosition=new ReviewedMonthlyCashflowPosition("cash-position-"+customer,Guid.NewGuid().ToString("N"),customer,new(new(2026,9,30),"INR",new[]{expense},true,true),new[]{new MonthlyCashflowPositionLine(expense,null)},"actor-liquidity",DateTimeOffset.UtcNow);var cashflow=new MonthlyCashflowCalculation("cashflow-"+customer,Guid.NewGuid().ToString("N"),customer,cashPosition,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED","0.00","600.00","-600.00",Array.Empty<string>(),"actor-liquidity",DateTimeOffset.UtcNow);
        var operation=Guid.NewGuid().ToString("N");var value=new LiquidityRatioCalculation("liquidity-"+Guid.NewGuid().ToString("N"),operation,customer,net,cashflow,new[]{asset.LineId},true,LiquidityRatioDefinition.Id,LiquidityRatioDefinition.Version,LiquidityRatioDefinition.DefinitionHash,LiquidityRatioDefinition.Semantics,"CALCULATED","1200.00","600.00","2.00",Array.Empty<string>(),"actor-liquidity",DateTimeOffset.UtcNow);var fingerprint="fingerprint";
        await using(var repository=new PostgresLiquidityRatioCalculations(rules)){Assert.True(NetWorthData.Same(value,await repository.CreateAsync("T001",customer,"actor-liquidity",fingerprint,value,default)));Assert.True(NetWorthData.Same(value,await repository.CreateAsync("T001",customer,"actor-liquidity",fingerprint,value,default)));}
        await using(var repository=new PostgresLiquidityRatioCalculations(rules)){Assert.True(NetWorthData.Same(value,await repository.ReadAsync("T001",customer,value.CalculationId,default)));Assert.Single((await repository.ListAsync("T001",customer,null,default)).Items);Assert.Null(await repository.ReadAsync("T002",customer,value.CalculationId,default));Assert.Null(await repository.ReadAsync("T001","foreign",value.CalculationId,default));Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repository.CreateAsync("T001",customer,"actor-liquidity","changed",value,default))).Status);}
        await using var sql=new NpgsqlConnection(Runtime("financial-rules"));var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_rules.liquidity_ratio_results SET payload=payload WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",update.SqlState);var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_rules.liquidity_ratio_results WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",delete.SqlState);
    }
}
