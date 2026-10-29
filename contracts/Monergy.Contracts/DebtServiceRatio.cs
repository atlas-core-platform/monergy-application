using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateDebtServiceRatio(string OperationId,string MonthlyCashflowCalculationId,
    IReadOnlyList<string> DebtPaymentLineIds,bool DebtPaymentsComplete);
public sealed record DebtServiceRatioCalculation(string CalculationId,string OperationId,string CustomerId,
    MonthlyCashflowCalculation SourceCalculation,IReadOnlyList<string> DebtPaymentLineIds,bool DebtPaymentsComplete,
    string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? MonthlyIncome,string? MonthlyDebtPayments,string? RatioPercent,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record DebtServiceRatioCalculationPage(IReadOnlyList<DebtServiceRatioCalculation> Items,bool HasMore,string? NextBeforeId);

public static class DebtServiceRatioDefinition
{
    public const string Id="monergy.monthly-debt-service-ratio";
    public const string Version="1.0.0";
    public const string Semantics="Selected customer-reviewed monthly debt payments divided by total customer-reviewed monthly income, multiplied by 100 and rounded to two decimal places away from zero. Requires one complete saved monthly cash-flow result and explicit confirmation that every monthly debt payment in that review is selected. This is not lender DTI/FOIR, underwriting, eligibility, advice or independently verified gross income.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class DebtServiceRatioData
{
    public static bool Request(CalculateDebtServiceRatio? value)=>value is {DebtPaymentLineIds:not null}&&
        MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Text(value.MonthlyCashflowCalculationId,80)&&
        value.DebtPaymentLineIds.Count<=MonthlyCashflowData.MaximumLines&&value.DebtPaymentLineIds.All(x=>MonthlyCashflowData.Operation(x))&&
        value.DebtPaymentLineIds.Distinct(StringComparer.Ordinal).Count()==value.DebtPaymentLineIds.Count;
    public static bool Calculation(DebtServiceRatioCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&
        MonthlyCashflowData.Calculation(value.SourceCalculation,customer)&&value.SourceCalculation.CalculationId==value.SourceCalculation.CalculationId&&
        value.DebtPaymentLineIds is not null&&value.DebtPaymentLineIds.Count<=MonthlyCashflowData.MaximumLines&&value.DebtPaymentLineIds.All(x=>MonthlyCashflowData.Operation(x))&&
        value.DebtPaymentLineIds.Distinct(StringComparer.Ordinal).Count()==value.DebtPaymentLineIds.Count&&value.DebtPaymentLineIds.All(id=>value.SourceCalculation.Position.Position.Lines.Any(line=>line.LineId==id&&line.Side=="EXPENSE"&&line.Amount is not null))&&
        value.RuleId==DebtServiceRatioDefinition.Id&&value.RuleVersion==DebtServiceRatioDefinition.Version&&value.DefinitionHash==DebtServiceRatioDefinition.DefinitionHash&&
        value.NumericSemantics==DebtServiceRatioDefinition.Semantics&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.DebtPaymentsComplete&&value.Issues.Count==0&&Money(value.MonthlyIncome)&&Money(value.MonthlyDebtPayments)&&Percent(value.RatioPercent)||
         value.State=="INSUFFICIENT_DATA"&&value.MonthlyIncome is null&&value.MonthlyDebtPayments is null&&value.RatioPercent is null&&value.Issues.Count>0&&value.Issues.Count<=3&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "MONTHLY_CASHFLOW_NOT_CALCULATED" or "DEBT_PAYMENTS_NOT_CONFIRMED" or "MONTHLY_INCOME_ZERO"));
    private static bool Money(string? value)=>value is not null&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
    private static bool Percent(string? value)=>value is not null&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
