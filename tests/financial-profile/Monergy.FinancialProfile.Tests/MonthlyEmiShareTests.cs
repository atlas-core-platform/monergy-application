using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.FinancialRules.Domain;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed class MonthlyEmiShareTests
{
    [Theory]
    [InlineData("10000.00",2500,750,"32.50")]
    [InlineData("3.00",1,0,"33.33")]
    public void ExactReviewedActiveLoanVersionsProduceDescriptiveRate(string income,decimal first,decimal second,string rate)
    {var result=Rule().Evaluate(Source(income),new[]{Loan("loan-1",first),Loan("loan-2",second)},true);Assert.Equal("CALCULATED",result.State);Assert.Equal(income,result.MonthlyIncome);Assert.Equal((first+second).ToString("F2",System.Globalization.CultureInfo.InvariantCulture),result.MonthlyEmi);Assert.Equal(rate,result.RatePercent);Assert.Empty(result.Issues);}
    [Fact]
    public void CompletenessZeroIncomeAndLifecycleFailClosedWithoutThresholds()
    {
        var incomplete=Rule().Evaluate(Source("1000.00"),new[]{Loan("loan-1",100)},false);Assert.Contains("ACTIVE_LOANS_NOT_CONFIRMED",incomplete.Issues);Assert.Null(incomplete.RatePercent);
        var zero=Rule().Evaluate(Source("0.00"),new[]{Loan("loan-1",100)},true);Assert.Contains("MONTHLY_INCOME_ZERO",zero.Issues);
        var closed=Rule().Evaluate(Source("1000.00"),new[]{Loan("loan-1",100) with{LifecycleStatus="CLOSED"}},true);Assert.Contains("LOAN_NOT_ACTIVE_FOR_MONTH",closed.Issues);
        var foreign=Rule().Evaluate(Source("1000.00"),new[]{Loan("loan-1",100) with{Currency="USD"}},true);Assert.Contains("CURRENCY_MISMATCH",foreign.Issues);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:MonthlyEmiShare:Formula","UNAPPROVED_THRESHOLD"}}).Build();Assert.Throws<InvalidOperationException>(()=>new MonthlyEmiShareRule(config));
    }
    private static MonthlyEmiShareRule Rule()=>new(new ConfigurationBuilder().Build());
    private static MonthlyCashflowCalculation Source(string income)
    {var day=new DateOnly(2026,9,30);var line=new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary",income,"INR",day,"Customer reviewed");var position=new ReviewedMonthlyCashflowPosition("position-1",Guid.NewGuid().ToString("N"),"customer-1",new(day,"INR",new[]{line},true,true,Household:"Customer household"),new[]{new MonthlyCashflowPositionLine(line,null)},"actor-1",DateTimeOffset.UtcNow);return new("cashflow-1",Guid.NewGuid().ToString("N"),"customer-1",position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED",income,"0.00",income,Array.Empty<string>(),"actor-1",DateTimeOffset.UtcNow);}
    private static ReviewedLoanRecord Loan(string id,decimal emi)
    {var day=new DateOnly(2026,9,30);return new(id,Guid.NewGuid().ToString("N"),"customer-1",new("Home loan","Bank","HOME",100000,80000,8,emi,60,new(2026,1,1),new(2030,12,31),day,"fact-1",1),"INR","provenance-1","document-1","evidence-1","actor-1",DateTimeOffset.UtcNow,2,"ACTIVE",day);}
}
