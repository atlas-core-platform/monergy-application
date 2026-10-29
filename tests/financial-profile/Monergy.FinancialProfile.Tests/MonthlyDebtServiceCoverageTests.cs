using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.FinancialRules.Domain;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed class MonthlyDebtServiceCoverageTests
{
    [Theory]
    [InlineData("10000.00","5000.00","5000.00",2500,"7500.00","3.00")]
    [InlineData("4000.00","7000.00","-3000.00",2000,"-1000.00","-0.50")]
    public void ExactReceiptsProduceDescriptiveCoverage(string income,string expenses,string balance,decimal emi,string available,string multiple)
    {var cashflow=Source(income,expenses,balance,"cashflow-1");var result=Rule().Evaluate(cashflow,Share(cashflow,emi),true);Assert.Equal("CALCULATED",result.State);Assert.Equal(income,result.MonthlyIncome);Assert.Equal(expenses,result.MonthlyExpenses);Assert.Equal(emi.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),result.MonthlyEmi);Assert.Equal(balance,result.MonthlyCashflowBalance);Assert.Equal(available,result.CashAvailableForDebtService);Assert.Equal(multiple,result.CoverageMultiple);Assert.Empty(result.Issues);}
    [Fact]
    public void ConfirmationZeroEmiMismatchedReceiptsAndUnapprovedFormulaFailClosed()
    {
        var cashflow=Source("1000.00","500.00","500.00","cashflow-1");var share=Share(cashflow,100);
        Assert.Contains("DEBT_SERVICE_IN_EXPENSES_NOT_CONFIRMED",Rule().Evaluate(cashflow,share,false).Issues);
        Assert.Contains("MONTHLY_EMI_ZERO",Rule().Evaluate(cashflow,Share(cashflow,0),true).Issues);
        Assert.Contains("SOURCE_RECEIPTS_DO_NOT_MATCH",Rule().Evaluate(Source("1000.00","500.00","500.00","cashflow-2"),share,true).Issues);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:MonthlyDebtServiceCoverage:Formula","UNAPPROVED_DSCR_THRESHOLD"}}).Build();Assert.Throws<InvalidOperationException>(()=>new MonthlyDebtServiceCoverageRule(config));
    }
    private static MonthlyDebtServiceCoverageRule Rule()=>new(new ConfigurationBuilder().Build());
    private static MonthlyCashflowCalculation Source(string income,string expenses,string balance,string id)
    {var day=new DateOnly(2026,9,30);var incomeLine=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary",income,"INR",day,"Customer reviewed");var expenseLine=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","HOUSEHOLD","Household",expenses,"INR",day,"Customer reviewed");var lines=new[]{incomeLine,expenseLine};var position=new ReviewedMonthlyCashflowPosition("position-"+id,Guid.NewGuid().ToString("N"),"customer-1",new(day,"INR",lines,true,true,Household:"Customer household"),lines.Select(x=>new MonthlyCashflowPositionLine(x,null)).ToArray(),"actor-1",DateTimeOffset.UtcNow);return new(id,Guid.NewGuid().ToString("N"),"customer-1",position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED",income,expenses,balance,Array.Empty<string>(),"actor-1",DateTimeOffset.UtcNow);}
    private static MonthlyEmiShareCalculation Share(MonthlyCashflowCalculation cashflow,decimal emi)
    {var loan=Loan(emi);var rate=decimal.Parse(cashflow.TotalIncome!,System.Globalization.CultureInfo.InvariantCulture)==0?"0.00":decimal.Round(emi/decimal.Parse(cashflow.TotalIncome!,System.Globalization.CultureInfo.InvariantCulture)*100m,2,MidpointRounding.AwayFromZero).ToString("F2",System.Globalization.CultureInfo.InvariantCulture);return new("emi-share-"+cashflow.CalculationId,Guid.NewGuid().ToString("N"),"customer-1",cashflow,new[]{loan},true,MonthlyEmiShareDefinition.Id,MonthlyEmiShareDefinition.Version,MonthlyEmiShareDefinition.DefinitionHash,MonthlyEmiShareDefinition.Semantics,"CALCULATED",cashflow.TotalIncome,emi.ToString("F2",System.Globalization.CultureInfo.InvariantCulture),rate,Array.Empty<string>(),"actor-1",DateTimeOffset.UtcNow);}
    private static ReviewedLoanRecord Loan(decimal emi){var day=new DateOnly(2026,9,30);return new("loan-1",Guid.NewGuid().ToString("N"),"customer-1",new("Home loan","Bank","HOME",100000,80000,8,emi,60,new(2026,1,1),new(2030,12,31),day,"fact-1",1),"INR","provenance-1","document-1","evidence-1","actor-1",DateTimeOffset.UtcNow,2,"ACTIVE",day);}
}
