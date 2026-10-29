using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record MonthlyNetCashflowRateEvaluation(string State,string? MonthlyIncome,string? MonthlyExpenses,string? NetCashflow,string? RatePercent,IReadOnlyList<string> Issues);
public sealed class MonthlyNetCashflowRateRule
{
    private static readonly string[] MonthlyCashflowNotCalculated = ["MONTHLY_CASHFLOW_NOT_CALCULATED"];
    private static readonly string[] MonthlyIncomeZero = ["MONTHLY_INCOME_ZERO"];
    public MonthlyNetCashflowRateRule(IConfiguration config)
    {
        if((config["Monergy:MonthlyNetCashflowRate:RuleVersion"]??MonthlyNetCashflowRateDefinition.Version)!=MonthlyNetCashflowRateDefinition.Version||
           (config["Monergy:MonthlyNetCashflowRate:Formula"]??"NET_CASHFLOW_DIVIDED_BY_INCOME")!="NET_CASHFLOW_DIVIDED_BY_INCOME")
            throw new InvalidOperationException("The configured monthly net cashflow rate formula/version is unavailable.");
    }
    public MonthlyNetCashflowRateEvaluation Evaluate(MonthlyCashflowCalculation source)
    {
        if(!MonthlyCashflowData.Calculation(source,source.CustomerId))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_SOURCE_INVALID",503);
        if(source.State!="CALCULATED")return new("INSUFFICIENT_DATA",null,null,null,null,MonthlyCashflowNotCalculated);
        decimal income=0,expenses=0;
        foreach(var line in source.Position.Position.Lines.Where(x=>x.Amount is not null))
        {var amount=decimal.Parse(line.Amount!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);if(line.Side=="INCOME")income=checked(income+amount);else expenses=checked(expenses+amount);}
        var net=checked(income-expenses);
        if(Money(income)!=source.TotalIncome||Money(expenses)!=source.TotalExpenses||Money(net)!=source.Balance)throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_SOURCE_INVALID",503);
        if(income==0)return new("INSUFFICIENT_DATA",null,null,null,null,MonthlyIncomeZero);
        var rate=decimal.Round(checked(net/income*100m),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",Money(income),Money(expenses),Money(net),Money(rate),Array.Empty<string>());
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
