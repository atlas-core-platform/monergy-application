using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateMonthlyDebtServiceCoverage(string OperationId,string MonthlyCashflowCalculationId,
    string MonthlyEmiShareCalculationId,bool DebtServiceIncludedInExpenses);
public sealed record MonthlyDebtServiceCoverageCalculation(string CalculationId,string OperationId,string CustomerId,
    MonthlyCashflowCalculation SourceMonthlyCashflow,MonthlyEmiShareCalculation SourceMonthlyEmiShare,
    bool DebtServiceIncludedInExpenses,string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? MonthlyIncome,string? MonthlyExpenses,string? MonthlyEmi,string? MonthlyCashflowBalance,
    string? CashAvailableForDebtService,string? CoverageMultiple,IReadOnlyList<string> Issues,string RecordedBy,DateTimeOffset RecordedAt);
public sealed record MonthlyDebtServiceCoverageCalculationPage(IReadOnlyList<MonthlyDebtServiceCoverageCalculation> Items,bool HasMore,string? NextBeforeId);

public static class MonthlyDebtServiceCoverageDefinition
{
    public const string Id="monergy.customer-reviewed-monthly-debt-service-coverage";
    public const string Version="1.0.0";
    public const string Semantics="For one exact saved calculated customer-reviewed Monthly Cashflow receipt and one exact saved calculated Monthly EMI Share receipt for that same cash-flow source, require customer confirmation that the reviewed monthly expenses include every selected EMI. Cash available for debt service equals reviewed monthly cash-flow balance plus selected monthly EMI; coverage multiple equals cash available for debt service divided by selected monthly EMI. Use exact checked decimal arithmetic and round the multiple to two decimal places away from zero. The browser cannot post income, expenses, balance, EMI, cash available or a multiple. This is descriptive customer-reviewed personal cash-flow coverage, not independently verified debt or income, formal lender or business DSCR, a threshold, eligibility or underwriting decision, recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class MonthlyDebtServiceCoverageData
{
    private static readonly string[] AllowedIssues=["MONTHLY_CASHFLOW_NOT_CALCULATED","MONTHLY_EMI_SHARE_NOT_CALCULATED","DEBT_SERVICE_IN_EXPENSES_NOT_CONFIRMED","SOURCE_RECEIPTS_DO_NOT_MATCH","MONTHLY_EMI_ZERO"];
    public static bool Request(CalculateMonthlyDebtServiceCoverage? value)=>value is not null&&MonthlyCashflowData.Operation(value.OperationId)&&
        MonthlyCashflowData.Text(value.MonthlyCashflowCalculationId,80)&&MonthlyCashflowData.Text(value.MonthlyEmiShareCalculationId,80);
    public static bool Calculation(MonthlyDebtServiceCoverageCalculation? value,string customer)=>value is {Issues:not null}&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Calculation(value.SourceMonthlyCashflow,customer)&&
        MonthlyEmiShareData.Calculation(value.SourceMonthlyEmiShare,customer)&&value.RuleId==MonthlyDebtServiceCoverageDefinition.Id&&
        value.RuleVersion==MonthlyDebtServiceCoverageDefinition.Version&&value.DefinitionHash==MonthlyDebtServiceCoverageDefinition.DefinitionHash&&
        value.NumericSemantics==MonthlyDebtServiceCoverageDefinition.Semantics&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&
        (value.State=="CALCULATED"&&value.DebtServiceIncludedInExpenses&&value.SourceMonthlyCashflow.State=="CALCULATED"&&value.SourceMonthlyEmiShare.State=="CALCULATED"&&
         MonthlyCashflowData.Same(value.SourceMonthlyCashflow,value.SourceMonthlyEmiShare.SourceMonthlyCashflow)&&value.Issues.Count==0&&
         Money(value.MonthlyIncome,false)&&Money(value.MonthlyExpenses,false)&&Money(value.MonthlyEmi,false)&&Money(value.MonthlyCashflowBalance,true)&&
         Money(value.CashAvailableForDebtService,true)&&Money(value.CoverageMultiple,true)&&value.MonthlyIncome==value.SourceMonthlyCashflow.TotalIncome&&
         value.MonthlyExpenses==value.SourceMonthlyCashflow.TotalExpenses&&value.MonthlyCashflowBalance==value.SourceMonthlyCashflow.Balance&&value.MonthlyEmi==value.SourceMonthlyEmiShare.MonthlyEmi||
         value.State=="INSUFFICIENT_DATA"&&value.MonthlyIncome is null&&value.MonthlyExpenses is null&&value.MonthlyEmi is null&&value.MonthlyCashflowBalance is null&&
         value.CashAvailableForDebtService is null&&value.CoverageMultiple is null&&value.Issues.Count is >0 and <=5&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(AllowedIssues.Contains));
    private static bool Money(string? value,bool signed)=>value is not null&&decimal.TryParse(value,signed?NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign:NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&
        (signed||amount>=0)&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
