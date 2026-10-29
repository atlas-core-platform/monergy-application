using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;
public sealed class MonthlyEmiSharePersistenceTests:PhysicalPostgresFixture
{
    [Fact]
    public async Task EmiShareReceiptSurvivesRestartIsTenantBoundIdempotentAndImmutable()
    {
        var rules=Configuration("FinancialRules");var customer="emi-share-"+Guid.NewGuid().ToString("N");var day=new DateOnly(2026,9,30);var line=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary","1000.00","INR",day,"Customer reviewed");var position=new ReviewedMonthlyCashflowPosition("position-"+customer,Guid.NewGuid().ToString("N"),customer,new(day,"INR",new[]{line},true,true,Household:"Customer household"),new[]{new MonthlyCashflowPositionLine(line,null)},"actor-emi",DateTimeOffset.UtcNow);var source=new MonthlyCashflowCalculation("cashflow-"+customer,Guid.NewGuid().ToString("N"),customer,position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED","1000.00","0.00","1000.00",Array.Empty<string>(),"actor-emi",DateTimeOffset.UtcNow);var loan=new ReviewedLoanRecord("loan-1",Guid.NewGuid().ToString("N"),customer,new("Loan","Bank","HOME",10000,8000,8,250,36,new(2026,1,1),new(2029,1,1),day,"fact-1",1),"INR","provenance-1","document-1","evidence-1","actor-emi",DateTimeOffset.UtcNow,2,"ACTIVE",day);
        var operation=Guid.NewGuid().ToString("N");var value=new MonthlyEmiShareCalculation("emi-share-"+Guid.NewGuid().ToString("N"),operation,customer,source,new[]{loan},true,MonthlyEmiShareDefinition.Id,MonthlyEmiShareDefinition.Version,MonthlyEmiShareDefinition.DefinitionHash,MonthlyEmiShareDefinition.Semantics,"CALCULATED","1000.00","250.00","25.00",Array.Empty<string>(),"actor-emi",DateTimeOffset.UtcNow);const string fingerprint="fingerprint";
        await using(var repository=new PostgresMonthlyEmiShareCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-emi",fingerprint,value,default)));Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-emi",fingerprint,value,default)));}
        await using(var repository=new PostgresMonthlyEmiShareCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.ReadAsync("T001",customer,value.CalculationId,default)));Assert.Single((await repository.ListAsync("T001",customer,null,default)).Items);Assert.Null(await repository.ReadAsync("T002",customer,value.CalculationId,default));Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repository.CreateAsync("T001",customer,"actor-emi","changed",value,default))).Status);}
        await using var sql=new NpgsqlConnection(Runtime("financial-rules"));var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_rules.monthly_emi_share_results SET payload=payload WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",update.SqlState);var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_rules.monthly_emi_share_results WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",delete.SqlState);
    }
}
