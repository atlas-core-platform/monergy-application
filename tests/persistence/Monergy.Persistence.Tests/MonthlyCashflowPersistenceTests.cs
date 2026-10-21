using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
using Monergy.Services.FinancialProfile.Infrastructure;
using Monergy.Services.FinancialRules.Application;
using Monergy.Services.FinancialRules.Domain;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;
public sealed partial class PersistentFoundationTests
{
    [Fact]
    public async Task MonthlyCashflowReviewAndCalculationSurviveRestartKeepOriginalReceiptsAndDenyRuntimeRewrite()
    {
        var profile=Configuration("FinancialProfile");var rules=Configuration("FinancialRules");var customer="cashflow-"+Guid.NewGuid().ToString("N");
        var line=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","BANK_ACCOUNT","Savings","1000.10","INR",new(2026,9,30),"Reviewed statement");
        var input=new CreateMonthlyCashflowPosition(Guid.NewGuid().ToString("N"),new(new(2026,9,30),"INR",new[]{line},true,true));ReviewedMonthlyCashflowPosition position;MonthlyCashflowCalculation result;CalculateMonthlyCashflow request;
        await using(var positions=new PostgresMonthlyCashflowPositions(profile))
        await using(var calculations=new PostgresMonthlyCashflowCalculations(rules))
        {
            var owner=new MonthlyCashflowPositionsApplication(positions,new InMemoryIncomeRecords(),new InMemoryExpenseRecords(),TimeProvider.System);
            position=await owner.CreateAsync("T001",customer,"actor-cashflow",input,default);request=new(Guid.NewGuid().ToString("N"),position.PositionId);
            result=await new MonthlyCashflowCalculationsApplication(new PersistentCashflowPositionReader(owner),calculations,new MonthlyCashflowRule(rules),TimeProvider.System).CalculateAsync("T001","session",customer,"actor-cashflow",request,default);
            Assert.Equal("1000.10",result.Balance);
        }
        await using(var positions=new PostgresMonthlyCashflowPositions(profile))
        await using(var calculations=new PostgresMonthlyCashflowCalculations(rules))
        {
            var owner=new MonthlyCashflowPositionsApplication(positions,new InMemoryIncomeRecords(),new InMemoryExpenseRecords(),TimeProvider.System);
            Assert.True(MonthlyCashflowData.Same(position,await owner.CreateAsync("T001",customer,"actor-cashflow",input,default)));
            var calculator=new MonthlyCashflowCalculationsApplication(new PersistentCashflowPositionReader(owner),calculations,new MonthlyCashflowRule(rules),TimeProvider.System);
            Assert.True(MonthlyCashflowData.Same(result,await calculator.CalculateAsync("T001","session",customer,"actor-cashflow",request,default)));
            Assert.True(MonthlyCashflowData.Same(result,await calculator.ReadAsync("T001","session",customer,result.CalculationId,default)));
            Assert.Single((await owner.ListAsync("T001",customer,null,default)).Items);Assert.Single((await calculator.ListAsync("T001","session",customer,null,default)).Items);
            Assert.Null(await positions.ReadAsync("T002",customer,position.PositionId,default));Assert.Null(await calculations.ReadAsync("T001","foreign",result.CalculationId,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-cashflow",input with {Position=input.Position with {IncomeComplete=false}},default))).Status);
        }
        foreach(var item in new[]{("financial-profile","financial_profile.monthly_cashflow_positions","position_id",position.PositionId),("financial-rules","financial_rules.monthly_cashflow_results","calculation_id",result.CalculationId)})
        {
            await using var sql=new NpgsqlConnection(Runtime(item.Item1));
            var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE "+item.Item2+" SET payload=payload WHERE "+item.Item3+"=@Id;",new {Id=item.Item4}));Assert.Equal("42501",update.SqlState);
            var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM "+item.Item2+" WHERE "+item.Item3+"=@Id;",new {Id=item.Item4}));Assert.Equal("42501",delete.SqlState);
        }
    }
    private sealed class PersistentCashflowPositionReader(MonthlyCashflowPositionsApplication owner):IMonthlyCashflowPositionReader
    {public Task<ReviewedMonthlyCashflowPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>owner.ReadAsync(tenant,customer,id,ct);}
}
