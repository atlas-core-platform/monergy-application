using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateDebtToIncome(string OperationId,string DebtServiceRatioCalculationId,bool GrossIncomeConfirmed);
public sealed record DebtToIncomeCalculation(string CalculationId,string OperationId,string CustomerId,
    DebtServiceRatioCalculation SourceDebtServiceRatio,bool GrossIncomeConfirmed,string TimeBasis,string IncomeBasis,
    string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? MonthlyDebtObligations,string? GrossMonthlyIncome,string? RatioPercent,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record DebtToIncomeCalculationPage(IReadOnlyList<DebtToIncomeCalculation> Items,bool HasMore,string? NextBeforeId);

public static class DebtToIncomeDefinition
{
    public const string Id="monergy.customer-reviewed-monthly-debt-to-income";
    public const string Version="1.0.0";
    public const string TimeBasis="MONTHLY";
    public const string IncomeBasis="GROSS_INCOME";
    public const string Semantics="Selected customer-reviewed monthly debt obligations from one exact calculated debt-service receipt, divided by that receipt's total reviewed monthly income after the customer explicitly confirms it represents gross monthly income before deductions, multiplied by 100 and rounded to two decimal places away from zero. The time basis is fixed to monthly. This is customer-reviewed DTI, not independent verification, lender DTI/FOIR, underwriting, eligibility, a credit decision, recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+TimeBasis+"\n"+IncomeBasis+"\n"+Semantics))).ToLowerInvariant();
}

public static class DebtToIncomeData
{
    public static bool Request(CalculateDebtToIncome? value)=>value is not null&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Text(value.DebtServiceRatioCalculationId,80);
    public static bool Calculation(DebtToIncomeCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&DebtServiceRatioData.Calculation(value.SourceDebtServiceRatio,customer)&&
        value.TimeBasis==DebtToIncomeDefinition.TimeBasis&&value.IncomeBasis==DebtToIncomeDefinition.IncomeBasis&&
        value.RuleId==DebtToIncomeDefinition.Id&&value.RuleVersion==DebtToIncomeDefinition.Version&&value.DefinitionHash==DebtToIncomeDefinition.DefinitionHash&&
        value.NumericSemantics==DebtToIncomeDefinition.Semantics&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.GrossIncomeConfirmed&&value.SourceDebtServiceRatio.State=="CALCULATED"&&value.SourceDebtServiceRatio.DebtPaymentsComplete&&value.Issues.Count==0&&
         Money(value.MonthlyDebtObligations)&&Money(value.GrossMonthlyIncome)&&Percent(value.RatioPercent)||
         value.State=="INSUFFICIENT_DATA"&&value.MonthlyDebtObligations is null&&value.GrossMonthlyIncome is null&&value.RatioPercent is null&&value.Issues.Count>0&&value.Issues.Count<=3&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "SOURCE_DEBT_SERVICE_RATIO_NOT_CALCULATED" or "GROSS_MONTHLY_INCOME_NOT_CONFIRMED" or "GROSS_MONTHLY_INCOME_ZERO"));
    private static bool Money(string? value)=>value is not null&&value.Length<=20&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
    private static bool Percent(string? value)=>value is not null&&value.Length<=32&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
