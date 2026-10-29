using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;
public sealed class AssetAllocationPersistenceTests:PhysicalPostgresFixture
{
    [Fact]
    public async Task AssetAllocationReceiptSurvivesRestartIsTenantBoundAndCannotBeRewritten()
    {
        var rules=Configuration("FinancialRules");var customer="allocation-"+Guid.NewGuid().ToString("N");var asset=new NetWorthLineDraft(Guid.NewGuid().ToString("N"),"ASSET","CASH","Cash","1200.00","INR",new(2026,10,1),"Reviewed cash");var position=new ReviewedNetWorthPosition("net-position-"+customer,Guid.NewGuid().ToString("N"),customer,new(new(2026,10,1),"INR",new[]{asset},true,true),new[]{new NetWorthPositionLine(asset,null)},"actor-allocation",DateTimeOffset.UtcNow);var net=new NetWorthCalculation("networth-"+customer,Guid.NewGuid().ToString("N"),customer,position,NetWorthDefinition.Id,NetWorthDefinition.Version,NetWorthDefinition.DefinitionHash,NetWorthDefinition.Semantics,"CALCULATED","1200.00","0.00","1200.00",Array.Empty<string>(),"actor-allocation",DateTimeOffset.UtcNow);
        var operation=Guid.NewGuid().ToString("N");var value=new AssetAllocationCalculation("allocation-"+Guid.NewGuid().ToString("N"),operation,customer,net,new[]{new AssetAllocationCategory("CASH","1200.00","100.00")},AssetAllocationDefinition.Id,AssetAllocationDefinition.Version,AssetAllocationDefinition.DefinitionHash,AssetAllocationDefinition.Semantics,"CALCULATED","1200.00",Array.Empty<string>(),"actor-allocation",DateTimeOffset.UtcNow);const string fingerprint="fingerprint";
        await using(var repository=new PostgresAssetAllocationCalculations(rules)){Assert.True(NetWorthData.Same(value,await repository.CreateAsync("T001",customer,"actor-allocation",fingerprint,value,default)));Assert.True(NetWorthData.Same(value,await repository.CreateAsync("T001",customer,"actor-allocation",fingerprint,value,default)));}
        await using(var repository=new PostgresAssetAllocationCalculations(rules)){Assert.True(NetWorthData.Same(value,await repository.ReadAsync("T001",customer,value.CalculationId,default)));Assert.Single((await repository.ListAsync("T001",customer,null,default)).Items);Assert.Null(await repository.ReadAsync("T002",customer,value.CalculationId,default));Assert.Null(await repository.ReadAsync("T001","foreign",value.CalculationId,default));Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repository.CreateAsync("T001",customer,"actor-allocation","changed",value,default))).Status);}
        await using var sql=new NpgsqlConnection(Runtime("financial-rules"));var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_rules.asset_allocation_results SET payload=payload WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",update.SqlState);var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_rules.asset_allocation_results WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",delete.SqlState);
    }
}
