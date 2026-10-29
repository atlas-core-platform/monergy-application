using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record LiquidityRatioEvaluation(string State,string? LiquidAssets,string? MonthlyExpenses,string? RatioMonths,IReadOnlyList<string> Issues);
public sealed class LiquidityRatioRule
{
    public LiquidityRatioRule(IConfiguration config)
    {if((config["Monergy:LiquidityRatio:RuleVersion"]??LiquidityRatioDefinition.Version)!=LiquidityRatioDefinition.Version)throw new InvalidOperationException("The configured liquidity-ratio rule version is unavailable.");}
    public LiquidityRatioEvaluation Evaluate(NetWorthCalculation netWorth,MonthlyCashflowCalculation cashflow,IReadOnlyList<string> selected,bool complete)
    {
        if(!NetWorthData.Calculation(netWorth,netWorth.CustomerId)||!MonthlyCashflowData.Calculation(cashflow,cashflow.CustomerId)||netWorth.CustomerId!=cashflow.CustomerId)throw new TenantBoundaryException("LIQUIDITY_RATIO_SOURCE_INVALID",503);
        var eligible=netWorth.Position.Position.Lines.Where(x=>x.Side=="ASSET"&&x.Kind is "CASH" or "BANK_ACCOUNT"&&x.Amount is not null).ToArray();
        var lines=eligible.Where(x=>selected.Contains(x.LineId,StringComparer.Ordinal)).ToArray();
        if(lines.Length!=selected.Count)throw new TenantBoundaryException("LIQUIDITY_RATIO_SELECTION_INVALID",400);
        var issues=new List<string>();
        if(netWorth.State!="CALCULATED")issues.Add("NET_WORTH_NOT_CALCULATED");
        if(cashflow.State!="CALCULATED")issues.Add("MONTHLY_CASHFLOW_NOT_CALCULATED");
        if(!complete)issues.Add("LIQUID_ASSETS_NOT_CONFIRMED");
        if(complete&&!eligible.Select(x=>x.LineId).Order(StringComparer.Ordinal).SequenceEqual(selected.Order(StringComparer.Ordinal)))issues.Add("LIQUID_ASSET_SELECTION_INCOMPLETE");
        if(netWorth.Position.Position.Currency!=cashflow.Position.Position.Currency)issues.Add("CURRENCIES_DIFFER");
        if(cashflow.State=="CALCULATED"&&cashflow.TotalExpenses=="0.00")issues.Add("MONTHLY_EXPENSES_ZERO");
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues);
        decimal liquid=0;foreach(var line in lines)liquid=checked(liquid+decimal.Parse(line.Amount!,CultureInfo.InvariantCulture));
        var expenses=decimal.Parse(cashflow.TotalExpenses!,CultureInfo.InvariantCulture);
        var ratio=decimal.Round(checked(liquid/expenses),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",liquid.ToString("F2",CultureInfo.InvariantCulture),expenses.ToString("F2",CultureInfo.InvariantCulture),ratio.ToString("F2",CultureInfo.InvariantCulture),Array.Empty<string>());
    }
}
