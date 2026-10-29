using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateMonthlyNetCashflowRate(string OperationId,string MonthlyCashflowCalculationId);
public sealed record MonthlyNetCashflowRateCalculation(string CalculationId,string OperationId,string CustomerId,
    MonthlyCashflowCalculation SourceMonthlyCashflow,string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,
    string State,string? MonthlyIncome,string? MonthlyExpenses,string? NetCashflow,string? RatePercent,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record MonthlyNetCashflowRateCalculationPage(IReadOnlyList<MonthlyNetCashflowRateCalculation> Items,bool HasMore,string? NextBeforeId);

public static class MonthlyNetCashflowRateDefinition
{
    public const string Id="monergy.monthly-net-cashflow-rate";
    public const string Version="1.0.0";
    public const string Semantics="For one exact calculated customer-reviewed completed Monthly Cashflow receipt, subtract total reviewed monthly expenses from total reviewed monthly income, divide the signed net cashflow by total reviewed monthly income and multiply by 100. Use exact checked decimal arithmetic and round the signed percentage to two decimal places away from zero. The browser cannot post income, expenses, balance or rate. This is a customer-reviewed historical monthly cashflow rate, not independently verified income or spending, a savings target, forecast, eligibility decision, recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class MonthlyNetCashflowRateData
{
    public static bool Request(CalculateMonthlyNetCashflowRate? value)=>value is not null&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Text(value.MonthlyCashflowCalculationId,80);
    public static bool Calculation(MonthlyNetCashflowRateCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Calculation(value.SourceMonthlyCashflow,customer)&&
        value.RuleId==MonthlyNetCashflowRateDefinition.Id&&value.RuleVersion==MonthlyNetCashflowRateDefinition.Version&&value.DefinitionHash==MonthlyNetCashflowRateDefinition.DefinitionHash&&
        value.NumericSemantics==MonthlyNetCashflowRateDefinition.Semantics&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.SourceMonthlyCashflow.State=="CALCULATED"&&value.Issues.Count==0&&Money(value.MonthlyIncome,false)&&Money(value.MonthlyExpenses,false)&&Money(value.NetCashflow,true)&&Money(value.RatePercent,true)&&
         value.MonthlyIncome==value.SourceMonthlyCashflow.TotalIncome&&value.MonthlyExpenses==value.SourceMonthlyCashflow.TotalExpenses&&value.NetCashflow==value.SourceMonthlyCashflow.Balance||
         value.State=="INSUFFICIENT_DATA"&&value.MonthlyIncome is null&&value.MonthlyExpenses is null&&value.NetCashflow is null&&value.RatePercent is null&&value.Issues.Count>0&&value.Issues.Count<=2&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "MONTHLY_CASHFLOW_NOT_CALCULATED" or "MONTHLY_INCOME_ZERO"));
    private static bool Money(string? value,bool signed)=>value is not null&&value.Length<=32&&decimal.TryParse(value,signed?NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign:NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount.ToString("F2",CultureInfo.InvariantCulture)==value&&(signed||amount>=0);
}
