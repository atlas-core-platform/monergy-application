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
    public async Task NetWorthReviewAndCalculationSurviveRestartKeepOriginalReceiptsAndDenyRuntimeRewrite()
    {
        var profile=Configuration("FinancialProfile");var rules=Configuration("FinancialRules");var customer="networth-"+Guid.NewGuid().ToString("N");
        var line=new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","BANK_ACCOUNT","Savings","1000.10","INR",new(2026,10,1),"Reviewed statement");
        var input=new CreateNetWorthPosition(Guid.NewGuid().ToString("N"),new(new(2026,10,1),"INR",new[]{line},true,true));ReviewedNetWorthPosition position;NetWorthCalculation result;CalculateNetWorth request;
        await using(var positions=new PostgresNetWorthPositions(profile))
        await using(var calculations=new PostgresNetWorthCalculations(rules))
        {
            var owner=new NetWorthPositionsApplication(positions,new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),new InMemoryLiabilityRecords(),TimeProvider.System);
            position=await owner.CreateAsync("T001",customer,"actor-networth",input,default);request=new(Guid.NewGuid().ToString("N"),position.PositionId);
            result=await new NetWorthCalculationsApplication(new PersistentPositionReader(owner),calculations,new NetWorthRule(rules),TimeProvider.System).CalculateAsync("T001","session",customer,"actor-networth",request,default);
            Assert.Equal("1000.10",result.NetWorth);
        }
        await using(var positions=new PostgresNetWorthPositions(profile))
        await using(var calculations=new PostgresNetWorthCalculations(rules))
        {
            var owner=new NetWorthPositionsApplication(positions,new InMemoryInvestmentRecords(),new InMemoryPropertyRecords(),new InMemoryVehicleRecords(),new InMemoryLiabilityRecords(),TimeProvider.System);
            Assert.True(NetWorthData.Same(position,await owner.CreateAsync("T001",customer,"actor-networth",input,default)));
            var calculator=new NetWorthCalculationsApplication(new PersistentPositionReader(owner),calculations,new NetWorthRule(rules),TimeProvider.System);
            Assert.True(NetWorthData.Same(result,await calculator.CalculateAsync("T001","session",customer,"actor-networth",request,default)));
            Assert.True(NetWorthData.Same(result,await calculator.ReadAsync("T001","session",customer,result.CalculationId,default)));
            Assert.Single((await owner.ListAsync("T001",customer,null,default)).Items);Assert.Single((await calculator.ListAsync("T001","session",customer,null,default)).Items);
            Assert.Null(await positions.ReadAsync("T002",customer,position.PositionId,default));Assert.Null(await calculations.ReadAsync("T001","foreign",result.CalculationId,default));
            Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>owner.CreateAsync("T001",customer,"actor-networth",input with {Position=input.Position with {AssetsComplete=false}},default))).Status);
        }
        foreach(var item in new[]{("financial-profile","financial_profile.net_worth_positions","position_id",position.PositionId),("financial-rules","financial_rules.net_worth_results","calculation_id",result.CalculationId)})
        {
            await using var sql=new NpgsqlConnection(Runtime(item.Item1));
            var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE "+item.Item2+" SET payload=payload WHERE "+item.Item3+"=@Id;",new {Id=item.Item4}));Assert.Equal("42501",update.SqlState);
            var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM "+item.Item2+" WHERE "+item.Item3+"=@Id;",new {Id=item.Item4}));Assert.Equal("42501",delete.SqlState);
        }
    }
    private sealed class PersistentPositionReader(NetWorthPositionsApplication owner):INetWorthPositionReader
    {public Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)=>owner.ReadAsync(tenant,customer,id,ct);}
}
