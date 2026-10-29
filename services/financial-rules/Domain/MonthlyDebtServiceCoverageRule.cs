using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record MonthlyDebtServiceCoverageEvaluation(string State,string? MonthlyIncome,string? MonthlyExpenses,string? MonthlyEmi,
    string? MonthlyCashflowBalance,string? CashAvailableForDebtService,string? CoverageMultiple,IReadOnlyList<string> Issues);
public sealed class MonthlyDebtServiceCoverageRule
{
    private static readonly string[] MonthlyEmiZero = ["MONTHLY_EMI_ZERO"];
    public MonthlyDebtServiceCoverageRule(IConfiguration config)
    {
        if((config["Monergy:MonthlyDebtServiceCoverage:RuleVersion"]??MonthlyDebtServiceCoverageDefinition.Version)!=MonthlyDebtServiceCoverageDefinition.Version||
           (config["Monergy:MonthlyDebtServiceCoverage:Formula"]??"CASHFLOW_BALANCE_PLUS_EMI_DIVIDED_BY_EMI")!="CASHFLOW_BALANCE_PLUS_EMI_DIVIDED_BY_EMI")
            throw new InvalidOperationException("The configured monthly debt-service coverage formula/version is unavailable.");
    }
    public MonthlyDebtServiceCoverageEvaluation Evaluate(MonthlyCashflowCalculation cashflow,MonthlyEmiShareCalculation emiShare,bool included)
    {
        if(!MonthlyCashflowData.Calculation(cashflow,cashflow.CustomerId)||!MonthlyEmiShareData.Calculation(emiShare,cashflow.CustomerId))
            throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_SOURCE_INVALID",503);
        var issues=new List<string>();
        if(cashflow.State!="CALCULATED")issues.Add("MONTHLY_CASHFLOW_NOT_CALCULATED");
        if(emiShare.State!="CALCULATED")issues.Add("MONTHLY_EMI_SHARE_NOT_CALCULATED");
        if(!included)issues.Add("DEBT_SERVICE_IN_EXPENSES_NOT_CONFIRMED");
        if(!MonthlyCashflowData.Same(cashflow,emiShare.SourceMonthlyCashflow))issues.Add("SOURCE_RECEIPTS_DO_NOT_MATCH");
        if(issues.Count>0)return Empty(issues);
        var emi=decimal.Parse(emiShare.MonthlyEmi!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        if(emi==0)return Empty(MonthlyEmiZero);
        var income=decimal.Parse(cashflow.TotalIncome!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        var expenses=decimal.Parse(cashflow.TotalExpenses!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        var balance=decimal.Parse(cashflow.Balance!,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);
        var available=checked(balance+emi);var coverage=decimal.Round(checked(available/emi),2,MidpointRounding.AwayFromZero);
        return new("CALCULATED",Money(income),Money(expenses),Money(emi),Money(balance),Money(available),Money(coverage),Array.Empty<string>());
    }
    private static MonthlyDebtServiceCoverageEvaluation Empty(IReadOnlyList<string> issues)=>new("INSUFFICIENT_DATA",null,null,null,null,null,null,issues.Distinct(StringComparer.Ordinal).ToArray());
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
