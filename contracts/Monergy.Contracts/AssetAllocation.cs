using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateAssetAllocation(string OperationId,string NetWorthCalculationId);
public sealed record AssetAllocationCategory(string Category,string Amount,string Percentage);
public sealed record AssetAllocationCalculation(string CalculationId,string OperationId,string CustomerId,
    NetWorthCalculation SourceNetWorth,IReadOnlyList<AssetAllocationCategory> Categories,
    string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? TotalAssets,IReadOnlyList<string> Issues,string RecordedBy,DateTimeOffset RecordedAt);
public sealed record AssetAllocationCalculationPage(IReadOnlyList<AssetAllocationCalculation> Items,bool HasMore,string? NextBeforeId);

public static class AssetAllocationDefinition
{
    public const string Id="monergy.asset-allocation";
    public const string Version="1.0.0";
    public const string Semantics="Group every positive asset value in one exact calculated customer-reviewed Net Worth inventory by its canonical asset type. Each category amount is divided by total assets and its percentage is independently rounded to two decimal places away from zero; displayed percentages can therefore differ from 100.00 by a rounding cent. Categories and values are resolved from the saved source and cannot be posted by the browser. This is historical composition of customer-reviewed values, not verified ownership or valuation, a target allocation, diversification assessment, eligibility, recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class AssetAllocationData
{
    private static readonly string[] Categories=["BANK_ACCOUNT","CASH","INVESTMENT","OTHER_ASSET","PROPERTY","VEHICLE"];
    public static bool Request(CalculateAssetAllocation? value)=>value is not null&&NetWorthData.Operation(value.OperationId)&&NetWorthData.Text(value.NetWorthCalculationId,80);
    public static bool Calculation(AssetAllocationCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&
        NetWorthData.Text(value.CalculationId,80)&&NetWorthData.Operation(value.OperationId)&&NetWorthData.Calculation(value.SourceNetWorth,customer)&&
        value.RuleId==AssetAllocationDefinition.Id&&value.RuleVersion==AssetAllocationDefinition.Version&&value.DefinitionHash==AssetAllocationDefinition.DefinitionHash&&
        value.NumericSemantics==AssetAllocationDefinition.Semantics&&NetWorthData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&value.Categories is not null&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.SourceNetWorth.State=="CALCULATED"&&Money(value.TotalAssets)&&value.TotalAssets==value.SourceNetWorth.TotalAssets&&value.Issues.Count==0&&
         value.Categories.Count is >=1 and <=6&&value.Categories.Select(x=>x.Category).SequenceEqual(value.Categories.Select(x=>x.Category).Order(StringComparer.Ordinal))&&
         value.Categories.Select(x=>x.Category).Distinct(StringComparer.Ordinal).Count()==value.Categories.Count&&value.Categories.All(Category)&&
         value.Categories.Sum(x=>decimal.Parse(x.Amount,CultureInfo.InvariantCulture))==decimal.Parse(value.TotalAssets!,CultureInfo.InvariantCulture)||
         value.State=="INSUFFICIENT_DATA"&&value.TotalAssets is null&&value.Categories.Count==0&&value.Issues.Count>0&&value.Issues.Count<=2&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "NET_WORTH_NOT_CALCULATED" or "TOTAL_ASSETS_ZERO"));
    private static bool Category(AssetAllocationCategory value)=>Categories.Contains(value.Category,StringComparer.Ordinal)&&Money(value.Amount)&&decimal.Parse(value.Amount,CultureInfo.InvariantCulture)>0&&Percentage(value.Percentage);
    private static bool Money(string? value)=>value is not null&&value.Length<=20&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
    private static bool Percentage(string? value)=>value is not null&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount is >=0 and <=100&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
