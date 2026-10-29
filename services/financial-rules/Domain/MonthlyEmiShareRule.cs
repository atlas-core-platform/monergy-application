using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record MonthlyEmiShareEvaluation(string State,string? MonthlyIncome,string? MonthlyEmi,string? RatePercent,IReadOnlyList<string> Issues);
public sealed class MonthlyEmiShareRule
{
    public MonthlyEmiShareRule(IConfiguration config)
    {
        if((config["Monergy:MonthlyEmiShare:RuleVersion"]??MonthlyEmiShareDefinition.Version)!=MonthlyEmiShareDefinition.Version||
           (config["Monergy:MonthlyEmiShare:Formula"]??"ACTIVE_REVIEWED_EMI_DIVIDED_BY_MONTHLY_INCOME")!="ACTIVE_REVIEWED_EMI_DIVIDED_BY_MONTHLY_INCOME")
            throw new InvalidOperationException("The configured monthly EMI share formula/version is unavailable.");
    }
    public MonthlyEmiShareEvaluation Evaluate(MonthlyCashflowCalculation source,IReadOnlyList<ReviewedLoanRecord> loans,bool complete)
    {
        if(!MonthlyCashflowData.Calculation(source,source.CustomerId)||loans is null||loans.Any(x=>!MonthlyEmiShareData.Loan(x,source.CustomerId)))
            throw new TenantBoundaryException("MONTHLY_EMI_SHARE_SOURCE_INVALID",503);
        var issues=new List<string>();
        if(source.State!="CALCULATED")issues.Add("MONTHLY_CASHFLOW_NOT_CALCULATED");
        if(!complete)issues.Add("ACTIVE_LOANS_NOT_CONFIRMED");
        if(loans.Count==0)issues.Add("NO_ACTIVE_LOANS_SELECTED");
        if(source.State=="CALCULATED")
        {
            var monthEnd=source.Position.Position.AsOfDate;
            if(loans.Any(x=>x.LifecycleStatus!="ACTIVE"||x.LifecycleAsOfDate is null||x.LifecycleAsOfDate>monthEnd||x.Loan.StartDate>monthEnd||x.Loan.EndDate<monthEnd||x.Loan.BalanceAsOfDate>monthEnd))issues.Add("LOAN_NOT_ACTIVE_FOR_MONTH");
            if(loans.Any(x=>x.Currency!=source.Position.Position.Currency))issues.Add("CURRENCY_MISMATCH");
        }
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues.Distinct(StringComparer.Ordinal).ToArray());
        var income=decimal.Parse(source.TotalIncome!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        if(income==0)return new("INSUFFICIENT_DATA",null,null,null,new[]{"MONTHLY_INCOME_ZERO"});
        decimal emi=0;foreach(var loan in loans)emi=checked(emi+loan.Loan.Emi);
        var rate=decimal.Round(checked(emi/income*100m),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",Money(income),Money(emi),Money(rate),Array.Empty<string>());
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
