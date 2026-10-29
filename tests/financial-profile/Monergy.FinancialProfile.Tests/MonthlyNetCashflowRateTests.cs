using Microsoft.Extensions.Configuration;
using Monergy.Contracts;
using Monergy.Services.FinancialRules.Domain;
using Xunit;
namespace Monergy.FinancialProfile.Tests;

public sealed class MonthlyNetCashflowRateTests
{
    [Theory]
    [InlineData("10000.00","7000.00","3000.00","30.00")]
    [InlineData("1000.00","1250.00","-250.00","-25.00")]
    [InlineData("3.00","2.00","1.00","33.33")]
    public void ExactSavedCashflowProducesSignedRate(string income,string expenses,string net,string rate)
    {
        var result=Rule().Evaluate(Source(income,expenses,net));
        Assert.Equal("CALCULATED",result.State);Assert.Equal(income,result.MonthlyIncome);Assert.Equal(expenses,result.MonthlyExpenses);Assert.Equal(net,result.NetCashflow);Assert.Equal(rate,result.RatePercent);Assert.Empty(result.Issues);
    }
    [Fact]
    public void ZeroIncomeProducesNoPercentage()
    {
        var result=Rule().Evaluate(Source("0.00","100.00","-100.00"));Assert.Equal("INSUFFICIENT_DATA",result.State);Assert.Contains("MONTHLY_INCOME_ZERO",result.Issues);Assert.Null(result.RatePercent);
    }
    [Fact]
    public void AlteredSourceTotalsAndUnknownFormulaFailClosed()
    {
        Assert.Equal(503,Assert.Throws<Monergy.Platform.TenantBoundaryException>(()=>Rule().Evaluate(Source("100.00","20.00","81.00"))).Status);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Monergy:MonthlyNetCashflowRate:Formula","UNAPPROVED"}}).Build();Assert.Throws<InvalidOperationException>(()=>new MonthlyNetCashflowRateRule(config));
    }
    private static MonthlyNetCashflowRateRule Rule()=>new(new ConfigurationBuilder().Build());
    private static MonthlyCashflowCalculation Source(string income,string expenses,string net)
    {
        var day=new DateOnly(2026,9,30);var lines=new[]{new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"INCOME","SALARY","Salary",income,"INR",day,"Customer reviewed"),new MonthlyCashflowLineDraft(Guid.NewGuid().ToString("N"),"EXPENSE","HOUSEHOLD","Household",expenses,"INR",day,"Customer reviewed")};
        var position=new ReviewedMonthlyCashflowPosition("position-1",Guid.NewGuid().ToString("N"),"customer-1",new(day,"INR",lines,true,true,Household:"Customer household"),lines.Select(x=>new MonthlyCashflowPositionLine(x,null)).ToArray(),"actor-1",DateTimeOffset.UtcNow);
        return new("cashflow-1",Guid.NewGuid().ToString("N"),"customer-1",position,MonthlyCashflowDefinition.Id,MonthlyCashflowDefinition.Version,MonthlyCashflowDefinition.DefinitionHash,MonthlyCashflowDefinition.Semantics,"CALCULATED",income,expenses,net,Array.Empty<string>(),"actor-1",DateTimeOffset.UtcNow);
    }
}
