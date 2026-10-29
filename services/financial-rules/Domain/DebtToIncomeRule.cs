using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record DebtToIncomeEvaluation(string State,string? MonthlyDebtObligations,string? GrossMonthlyIncome,string? RatioPercent,IReadOnlyList<string> Issues);
public sealed class DebtToIncomeRule
{
    public DebtToIncomeRule(IConfiguration config)
    {if((config["Monergy:DebtToIncome:RuleVersion"]??DebtToIncomeDefinition.Version)!=DebtToIncomeDefinition.Version)throw new InvalidOperationException("The configured debt-to-income rule version is unavailable.");}
    public DebtToIncomeEvaluation Evaluate(DebtServiceRatioCalculation source,bool grossIncomeConfirmed)
    {
        if(!DebtServiceRatioData.Calculation(source,source.CustomerId))throw new TenantBoundaryException("DEBT_TO_INCOME_SOURCE_INVALID",503);
        var issues=new List<string>();
        if(source.State!="CALCULATED")issues.Add("SOURCE_DEBT_SERVICE_RATIO_NOT_CALCULATED");
        if(!grossIncomeConfirmed)issues.Add("GROSS_MONTHLY_INCOME_NOT_CONFIRMED");
        if(source.State=="CALCULATED"&&source.MonthlyIncome=="0.00")issues.Add("GROSS_MONTHLY_INCOME_ZERO");
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues);
        var selected=source.SourceCalculation.Position.Position.Lines.Where(x=>source.DebtPaymentLineIds.Contains(x.LineId,StringComparer.Ordinal)).ToArray();
        decimal obligations=0;foreach(var line in selected)obligations=checked(obligations+decimal.Parse(line.Amount!,CultureInfo.InvariantCulture));
        decimal income=0;foreach(var line in source.SourceCalculation.Position.Position.Lines.Where(x=>x.Side=="INCOME"&&x.Amount is not null))income=checked(income+decimal.Parse(line.Amount!,CultureInfo.InvariantCulture));
        if(obligations.ToString("F2",CultureInfo.InvariantCulture)!=source.MonthlyDebtPayments||income.ToString("F2",CultureInfo.InvariantCulture)!=source.MonthlyIncome)throw new TenantBoundaryException("DEBT_TO_INCOME_SOURCE_INVALID",503);
        if(income==0)throw new TenantBoundaryException("DEBT_TO_INCOME_SOURCE_INVALID",503);
        var ratio=decimal.Round(checked(obligations/income*100m),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",obligations.ToString("F2",CultureInfo.InvariantCulture),income.ToString("F2",CultureInfo.InvariantCulture),ratio.ToString("F2",CultureInfo.InvariantCulture),Array.Empty<string>());
    }
}
