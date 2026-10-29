using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record AssetAllocationEvaluation(string State,string? TotalAssets,IReadOnlyList<AssetAllocationCategory> Categories,IReadOnlyList<string> Issues);
public sealed class AssetAllocationRule
{
    private static readonly string[] NetWorthNotCalculated = ["NET_WORTH_NOT_CALCULATED"];
    private static readonly string[] TotalAssetsZero = ["TOTAL_ASSETS_ZERO"];
    public AssetAllocationRule(IConfiguration config)
    {
        if((config["Monergy:AssetAllocation:RuleVersion"]??AssetAllocationDefinition.Version)!=AssetAllocationDefinition.Version||
           (config["Monergy:AssetAllocation:Formula"]??"ASSET_TYPE_PERCENT_OF_TOTAL_ASSETS")!="ASSET_TYPE_PERCENT_OF_TOTAL_ASSETS")
            throw new InvalidOperationException("The configured asset-allocation formula/version is unavailable.");
    }
    public AssetAllocationEvaluation Evaluate(NetWorthCalculation source)
    {
        if(!NetWorthData.Calculation(source,source.CustomerId))throw new TenantBoundaryException("ASSET_ALLOCATION_SOURCE_INVALID",503);
        if(source.State!="CALCULATED")return new("INSUFFICIENT_DATA",null,Array.Empty<AssetAllocationCategory>(),NetWorthNotCalculated);
        var total=decimal.Parse(source.TotalAssets!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
        if(total==0)return new("INSUFFICIENT_DATA",null,Array.Empty<AssetAllocationCategory>(),TotalAssetsZero);
        var categories=source.Position.Position.Lines.Where(x=>x.Side=="ASSET"&&x.Amount is not null)
            .GroupBy(x=>x.Kind,StringComparer.Ordinal).Select(group=>new{Category=group.Key,Amount=group.Sum(x=>decimal.Parse(x.Amount!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture))})
            .Where(x=>x.Amount>0).OrderBy(x=>x.Category,StringComparer.Ordinal)
            .Select(x=>new AssetAllocationCategory(x.Category,Money(x.Amount),Money(decimal.Round(checked(x.Amount/total*100m),2,MidpointRounding.AwayFromZero)))).ToArray();
        return new("CALCULATED",Money(total),categories,Array.Empty<string>());
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
