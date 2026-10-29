using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateLiquidityRatio(string OperationId,string NetWorthCalculationId,string MonthlyCashflowCalculationId,
    IReadOnlyList<string> LiquidAssetLineIds,bool LiquidAssetsComplete);
public sealed record LiquidityRatioCalculation(string CalculationId,string OperationId,string CustomerId,
    NetWorthCalculation SourceNetWorth,MonthlyCashflowCalculation SourceMonthlyCashflow,
    IReadOnlyList<string> LiquidAssetLineIds,bool LiquidAssetsComplete,
    string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? LiquidAssets,string? MonthlyExpenses,string? RatioMonths,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record LiquidityRatioCalculationPage(IReadOnlyList<LiquidityRatioCalculation> Items,bool HasMore,string? NextBeforeId);

public static class LiquidityRatioDefinition
{
    public const string Id="monergy.liquidity-ratio";
    public const string Version="1.0.0";
    public const string Semantics="Sum every cash and bank-account asset in one exact calculated customer-reviewed Net Worth inventory, divided by total expenses from one exact calculated customer-reviewed completed calendar month, rounded to two decimal places away from zero. The selected asset identifiers must exactly match the eligible liquid-asset set and both sources must use the same currency. This is a customer-reviewed liquidity ratio, not independently verified liquidity, lender underwriting, eligibility, a recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class LiquidityRatioData
{
    public static bool Request(CalculateLiquidityRatio? value)=>value is {LiquidAssetLineIds:not null}&&
        NetWorthData.Operation(value.OperationId)&&NetWorthData.Text(value.NetWorthCalculationId,80)&&MonthlyCashflowData.Text(value.MonthlyCashflowCalculationId,80)&&
        value.LiquidAssetLineIds.Count<=NetWorthData.MaximumLines&&value.LiquidAssetLineIds.All(NetWorthData.Operation)&&
        value.LiquidAssetLineIds.Distinct(StringComparer.Ordinal).Count()==value.LiquidAssetLineIds.Count;
    public static bool Calculation(LiquidityRatioCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&
        NetWorthData.Text(value.CalculationId,80)&&NetWorthData.Operation(value.OperationId)&&
        NetWorthData.Calculation(value.SourceNetWorth,customer)&&MonthlyCashflowData.Calculation(value.SourceMonthlyCashflow,customer)&&
        value.LiquidAssetLineIds is not null&&value.LiquidAssetLineIds.Count<=NetWorthData.MaximumLines&&value.LiquidAssetLineIds.All(NetWorthData.Operation)&&
        value.LiquidAssetLineIds.Distinct(StringComparer.Ordinal).Count()==value.LiquidAssetLineIds.Count&&
        value.LiquidAssetLineIds.All(id=>value.SourceNetWorth.Position.Position.Lines.Any(line=>line.LineId==id&&line.Side=="ASSET"&&line.Kind is "CASH" or "BANK_ACCOUNT"&&line.Amount is not null))&&
        value.RuleId==LiquidityRatioDefinition.Id&&value.RuleVersion==LiquidityRatioDefinition.Version&&value.DefinitionHash==LiquidityRatioDefinition.DefinitionHash&&
        value.NumericSemantics==LiquidityRatioDefinition.Semantics&&NetWorthData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.LiquidAssetsComplete&&value.SourceNetWorth.State=="CALCULATED"&&value.SourceMonthlyCashflow.State=="CALCULATED"&&
         value.SourceNetWorth.Position.Position.Currency==value.SourceMonthlyCashflow.Position.Position.Currency&&value.Issues.Count==0&&
         Money(value.LiquidAssets)&&Money(value.MonthlyExpenses)&&Ratio(value.RatioMonths)||
         value.State=="INSUFFICIENT_DATA"&&value.LiquidAssets is null&&value.MonthlyExpenses is null&&value.RatioMonths is null&&value.Issues.Count>0&&value.Issues.Count<=6&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "NET_WORTH_NOT_CALCULATED" or "MONTHLY_CASHFLOW_NOT_CALCULATED" or "LIQUID_ASSETS_NOT_CONFIRMED" or "LIQUID_ASSET_SELECTION_INCOMPLETE" or "CURRENCIES_DIFFER" or "MONTHLY_EXPENSES_ZERO"));
    private static bool Money(string? value)=>value is not null&&value.Length<=20&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
    private static bool Ratio(string? value)=>value is not null&&value.Length<=32&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
