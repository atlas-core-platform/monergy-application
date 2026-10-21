using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NetWorthRecordReference(string Kind,string RecordId,int Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NetWorthLineDraft(string LineId,string Side,string Kind,string Name,string? Amount,
    string Currency,DateOnly? ValueDate,string SourceNote,NetWorthRecordReference? Record=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NetWorthPositionDraft(DateOnly AsOfDate,string Currency,IReadOnlyList<NetWorthLineDraft> Lines,
    bool AssetsComplete,bool LiabilitiesComplete,string? ReplacesPositionId=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateNetWorthPosition(string OperationId,NetWorthPositionDraft Position);
public sealed record NetWorthLineSource(string FinancialFactId,int SourceRevision,string ProvenanceId,
    string DocumentVersionId,string EvidenceId,string ReviewedBy,DateTimeOffset ReviewedAt);
public sealed record NetWorthPositionLine(NetWorthLineDraft Line,NetWorthLineSource? Source);
public sealed record ReviewedNetWorthPosition(string PositionId,string OperationId,string CustomerId,
    NetWorthPositionDraft Position,IReadOnlyList<NetWorthPositionLine> Lines,string RecordedBy,DateTimeOffset RecordedAt);
public sealed record NetWorthPositionPage(IReadOnlyList<ReviewedNetWorthPosition> Items,bool HasMore,string? NextBeforeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateNetWorth(string OperationId,string PositionId);
public sealed record NetWorthCalculation(string CalculationId,string OperationId,string CustomerId,
    ReviewedNetWorthPosition Position,string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,
    string State,string? TotalAssets,string? TotalLiabilities,string? NetWorth,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record NetWorthCalculationPage(IReadOnlyList<NetWorthCalculation> Items,bool HasMore,string? NextBeforeId);

// Wire-format and identity checks. Financial Rules alone evaluates the formula.
public static class NetWorthData
{
    public const int MaximumLines=200;
    public static bool Operation(string? value)=>value is {Length:32}&&Guid.TryParseExact(value,"N",out _)&&!value.Any(char.IsAsciiLetterUpper);
    public static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value==value.Trim()&&!value.Any(char.IsControl);
    public static bool Currency(string? value)=>value is {Length:3}&&value.All(char.IsAsciiLetterUpper);
    public static bool Money(string? value)=>value is {Length:>=4 and <=15}&&value[^3]=='.'&&
        value.Take(value.Length-3).All(char.IsAsciiDigit)&&value.TakeLast(2).All(char.IsAsciiDigit)&&
        (value.Length==4||value[0]!='0')&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount is >=0 and <=999999999999.99m;
    public static bool Date(DateOnly date,DateTimeOffset now)=>date.Year>=1900&&date<=DateOnly.FromDateTime(now.UtcDateTime.AddHours(14));
    public static bool Line(NetWorthLineDraft? line,DateTimeOffset now)=>line is not null&&Operation(line.LineId)&&Text(line.Name,120)&&Text(line.SourceNote,500)&&
        (line.Side=="ASSET"&&line.Kind is "CASH" or "BANK_ACCOUNT" or "INVESTMENT" or "PROPERTY" or "VEHICLE" or "OTHER_ASSET"||
         line.Side=="LIABILITY"&&line.Kind is "LOAN" or "CREDIT_CARD" or "OTHER_LIABILITY")&&
        Currency(line.Currency)&&(line.Amount is null||Money(line.Amount))&&(line.ValueDate is null||Date(line.ValueDate.Value,now))&&
        (line.Record is null||Text(line.Record.RecordId,80)&&line.Record.Revision>0&&
         (line.Side=="ASSET"&&line.Record.Kind==line.Kind&&line.Kind is "INVESTMENT" or "PROPERTY" or "VEHICLE"||line.Side=="LIABILITY"&&line.Record.Kind=="LIABILITY"));
    public static bool Draft(NetWorthPositionDraft? value,DateTimeOffset now)=>value is {Lines:not null}&&Date(value.AsOfDate,now)&&Currency(value.Currency)&&
        value.Lines.Count<=MaximumLines&&value.Lines.All(line=>Line(line,now))&&value.Lines.Select(x=>x.LineId).Distinct(StringComparer.Ordinal).Count()==value.Lines.Count&&
        value.Lines.Where(x=>x.Record is not null).Select(x=>x.Record!.Kind+"|"+x.Record.RecordId).Distinct(StringComparer.Ordinal).Count()==value.Lines.Count(x=>x.Record is not null)&&
        (value.ReplacesPositionId is null||Text(value.ReplacesPositionId,80));
    public static bool Bound(ReviewedNetWorthPosition? value,string customer,DateTimeOffset now)=>value is {Lines:not null}&&value.CustomerId==customer&&Text(value.PositionId,80)&&Operation(value.OperationId)&&Text(value.RecordedBy,160)&&value.RecordedAt!=default&&Draft(value.Position,now)&&
        value.Lines.Count==value.Position.Lines.Count&&value.Lines.Select(x=>x?.Line).SequenceEqual(value.Position.Lines)&&value.Lines.All(x=>x is not null&&
        (x.Line.Record is null?x.Source is null:x.Source is not null&&Text(x.Source.FinancialFactId,160)&&x.Source.SourceRevision>0&&Text(x.Source.ProvenanceId,160)&&Text(x.Source.DocumentVersionId,160)&&Text(x.Source.EvidenceId,160)&&Text(x.Source.ReviewedBy,160)&&x.Source.ReviewedAt!=default));
    public static bool Calculation(NetWorthCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&Text(value.CalculationId,80)&&NetWorthData.Operation(value.OperationId)&&NetWorthData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&NetWorthData.Bound(value.Position,customer,DateTimeOffset.UtcNow)&&
        value.RuleId==NetWorthDefinition.Id&&value.RuleVersion==NetWorthDefinition.Version&&value.DefinitionHash==NetWorthDefinition.DefinitionHash&&value.NumericSemantics==NetWorthDefinition.Semantics&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.Issues.Count==0&&Total(value.TotalAssets,false)&&Total(value.TotalLiabilities,false)&&Total(value.NetWorth,true)||value.State=="INSUFFICIENT_DATA"&&value.TotalAssets is null&&value.TotalLiabilities is null&&value.NetWorth is null&&value.Issues.Count>0&&value.Issues.Count<=7&&value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "ASSET_INVENTORY_NOT_CONFIRMED" or "LIABILITY_INVENTORY_NOT_CONFIRMED" or "MISSING_VALUE" or "MISSING_VALUE_DATE" or "MIXED_VALUE_DATES" or "MIXED_CURRENCIES" or "OVERLAPPING_SOURCE_RECORDS"));
    private static bool Total(string? value,bool signed)=>value is not null&&value.Length<=20&&decimal.TryParse(value,signed?NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign:NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount.ToString("F2",CultureInfo.InvariantCulture)==value&&(signed||amount>=0);
    public static bool Same<T>(T left,T right)=>JsonSerializer.Serialize(left,ContractJson.Options)==JsonSerializer.Serialize(right,ContractJson.Options);
}

public static class NetWorthDefinition
{
    public const string Id="monergy.net-worth";
    public const string Version="1.0.0";
    public const string Semantics="Sum reviewed asset values minus sum reviewed outstanding liabilities; exact checked decimal cents; one explicit valuation date and currency; no FX,carry-forward or rounding; complete customer-reviewed inventory required.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}
