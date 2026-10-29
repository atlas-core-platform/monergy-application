using Dapper;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Infrastructure;
using Npgsql;
using Xunit;
namespace Monergy.Persistence.Tests;
public sealed partial class PersistentFoundationTests
{
    [Fact]
    public async Task DebtToIncomeReceiptSurvivesRestartIsTenantBoundAndCannotBeRewritten()
    {
        var rules=Configuration("FinancialRules");var customer="dti-"+Guid.NewGuid().ToString("N");var income=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary","1000.00","INR",new(2026,9,30),"Reviewed salary");var debt=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","LOAN","Loan payment","250.00","INR",new(2026,9,30),"Reviewed debt");var position=new ReviewedMonthlyCashflowPosition("position-"+customer,Guid.NewGuid().ToString("N"),customer,new(new(2026,9,30),"INR",new[]{income,debt},true,true),new[]{new MonthlyCashflowPositionLine(income,null),new MonthlyCashflowPositionLine(debt,null)},"actor-dti",DateTimeOffset.UtcNow);var cashflow=new MonthlyCashflowCalculation("cashflow-"+customer,Guid.NewGuid().ToString("N"),customer,position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED","1000.00","250.00","750.00",Array.Empty<string>(),"actor-dti",DateTimeOffset.UtcNow);var debtRatio=new DebtServiceRatioCalculation("debt-ratio-"+customer,Guid.NewGuid().ToString("N"),customer,cashflow,new[]{debt.LineId},true,DebtServiceRatioDefinition.Id,DebtServiceRatioDefinition.Version,DebtServiceRatioDefinition.DefinitionHash,DebtServiceRatioDefinition.Semantics,"CALCULATED","1000.00","250.00","25.00",Array.Empty<string>(),"actor-dti",DateTimeOffset.UtcNow);
        var operation=Guid.NewGuid().ToString("N");var value=new DebtToIncomeCalculation("dti-"+Guid.NewGuid().ToString("N"),operation,customer,debtRatio,true,DebtToIncomeDefinition.TimeBasis,DebtToIncomeDefinition.IncomeBasis,DebtToIncomeDefinition.Id,DebtToIncomeDefinition.Version,DebtToIncomeDefinition.DefinitionHash,DebtToIncomeDefinition.Semantics,"CALCULATED","250.00","1000.00","25.00",Array.Empty<string>(),"actor-dti",DateTimeOffset.UtcNow);const string fingerprint="fingerprint";
        await using(var repository=new PostgresDebtToIncomeCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-dti",fingerprint,value,default)));Assert.True(MonthlyCashflowData.Same(value,await repository.CreateAsync("T001",customer,"actor-dti",fingerprint,value,default)));}
        await using(var repository=new PostgresDebtToIncomeCalculations(rules)){Assert.True(MonthlyCashflowData.Same(value,await repository.ReadAsync("T001",customer,value.CalculationId,default)));Assert.Single((await repository.ListAsync("T001",customer,null,default)).Items);Assert.Null(await repository.ReadAsync("T002",customer,value.CalculationId,default));Assert.Null(await repository.ReadAsync("T001","foreign",value.CalculationId,default));Assert.Equal(409,(await Assert.ThrowsAsync<TenantBoundaryException>(()=>repository.CreateAsync("T001",customer,"actor-dti","changed",value,default))).Status);}
        await using var sql=new NpgsqlConnection(Runtime("financial-rules"));var update=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("UPDATE financial_rules.debt_to_income_results SET payload=payload WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",update.SqlState);var delete=await Assert.ThrowsAsync<PostgresException>(()=>sql.ExecuteAsync("DELETE FROM financial_rules.debt_to_income_results WHERE calculation_id=@Id;",new{Id=value.CalculationId}));Assert.Equal("42501",delete.SqlState);
    }
}
