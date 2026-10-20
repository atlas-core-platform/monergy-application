using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;

public sealed record NetWorthEvaluation(string State,string? TotalAssets,string? TotalLiabilities,string? NetWorth,IReadOnlyList<string> Issues);
public sealed class NetWorthRule
{
    public const string Id=NetWorthDefinition.Id;
    public const string Version=NetWorthDefinition.Version;
    public const string Semantics=NetWorthDefinition.Semantics;
    public static string DefinitionHash=>NetWorthDefinition.DefinitionHash;
    public NetWorthRule(IConfiguration config)
    {
        // Central selection is deliberately restricted to the specified net-worth formula.
        if((config["Monergy:NetWorth:RuleVersion"]??Version)!=Version||
           (config["Monergy:NetWorth:Formula"]??"TOTAL_ASSETS_MINUS_TOTAL_LIABILITIES")!="TOTAL_ASSETS_MINUS_TOTAL_LIABILITIES")
            throw new InvalidOperationException("The configured net-worth formula/version is unavailable.");
    }
    public NetWorthEvaluation Evaluate(ReviewedNetWorthPosition position)
    {
        if(!NetWorthData.Bound(position,position.CustomerId,DateTimeOffset.UtcNow))throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",503);
        var issues=new List<string>();var input=position.Position;
        if(!input.AssetsComplete)issues.Add("ASSET_INVENTORY_NOT_CONFIRMED");
        if(!input.LiabilitiesComplete)issues.Add("LIABILITY_INVENTORY_NOT_CONFIRMED");
        if(input.Lines.Any(x=>x.Amount is null))issues.Add("MISSING_VALUE");
        if(input.Lines.Any(x=>x.ValueDate is null))issues.Add("MISSING_VALUE_DATE");
        if(input.Lines.Any(x=>x.ValueDate is not null&&x.ValueDate!=input.AsOfDate))issues.Add("MIXED_VALUE_DATES");
        if(input.Lines.Any(x=>x.Currency!=input.Currency))issues.Add("MIXED_CURRENCIES");
        var sources=position.Lines.Where(x=>x.Source is not null).Select(x=>x.Source!.FinancialFactId).ToArray();
        if(sources.Distinct(StringComparer.Ordinal).Count()!=sources.Length)issues.Add("OVERLAPPING_SOURCE_RECORDS");
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues);
        decimal assets=0,liabilities=0;
        foreach(var line in input.Lines)
        {var value=decimal.Parse(line.Amount!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);if(line.Side=="ASSET")assets=checked(assets+value);else liabilities=checked(liabilities+value);}
        return new("CALCULATED",Money(assets),Money(liabilities),Money(checked(assets-liabilities)),Array.Empty<string>());
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
