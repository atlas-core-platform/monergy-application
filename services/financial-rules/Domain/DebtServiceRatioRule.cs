using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record DebtServiceRatioEvaluation(string State,string? MonthlyIncome,string? MonthlyDebtPayments,string? RatioPercent,IReadOnlyList<string> Issues);
public sealed class DebtServiceRatioRule
{
    public DebtServiceRatioRule(IConfiguration config)
    {if((config["Monergy:DebtServiceRatio:RuleVersion"]??DebtServiceRatioDefinition.Version)!=DebtServiceRatioDefinition.Version)throw new InvalidOperationException("The configured debt-service ratio rule version is unavailable.");}
    public DebtServiceRatioEvaluation Evaluate(MonthlyCashflowCalculation source,IReadOnlyList<string> selected,bool complete)
    {
        if(!MonthlyCashflowData.Calculation(source,source.CustomerId))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_SOURCE_INVALID",503);
        var issues=new List<string>();
        if(source.State!="CALCULATED")issues.Add("MONTHLY_CASHFLOW_NOT_CALCULATED");
        if(!complete)issues.Add("DEBT_PAYMENTS_NOT_CONFIRMED");
        var lines=source.Position.Position.Lines.Where(x=>selected.Contains(x.LineId,StringComparer.Ordinal)).ToArray();
        if(lines.Length!=selected.Count||lines.Any(x=>x.Side!="EXPENSE"||x.Amount is null))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_SELECTION_INVALID",400);
        if(source.TotalIncome=="0.00")issues.Add("MONTHLY_INCOME_ZERO");
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues);
        var income=decimal.Parse(source.TotalIncome!,CultureInfo.InvariantCulture);decimal debt=0;
        foreach(var line in lines)debt=checked(debt+decimal.Parse(line.Amount!,CultureInfo.InvariantCulture));
        var ratio=decimal.Round(checked(debt/income*100m),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",income.ToString("F2",CultureInfo.InvariantCulture),debt.ToString("F2",CultureInfo.InvariantCulture),ratio.ToString("F2",CultureInfo.InvariantCulture),Array.Empty<string>());
    }
}
