using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonthlyCashflowRecordReference(string Kind,string RecordId,int Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonthlyCashflowLineDraft(string LineId,string Side,string Kind,string Name,string? Amount,
    string Currency,DateOnly? ValueDate,string SourceNote,MonthlyCashflowRecordReference? Record=null,string Member="Self");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonthlyCashflowPositionDraft(DateOnly AsOfDate,string Currency,IReadOnlyList<MonthlyCashflowLineDraft> Lines,
    bool IncomeComplete,bool ExpensesComplete,string? ReplacesPositionId=null,string Household="Self");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateMonthlyCashflowPosition(string OperationId,MonthlyCashflowPositionDraft Position);
public sealed record MonthlyCashflowLineSource(string FinancialFactId,int SourceRevision,string ProvenanceId,
    string DocumentVersionId,string EvidenceId,string ReviewedBy,DateTimeOffset ReviewedAt);
public sealed record MonthlyCashflowPositionLine(MonthlyCashflowLineDraft Line,MonthlyCashflowLineSource? Source);
public sealed record ReviewedMonthlyCashflowPosition(string PositionId,string OperationId,string CustomerId,
    MonthlyCashflowPositionDraft Position,IReadOnlyList<MonthlyCashflowPositionLine> Lines,string RecordedBy,DateTimeOffset RecordedAt);
public sealed record MonthlyCashflowPositionPage(IReadOnlyList<ReviewedMonthlyCashflowPosition> Items,bool HasMore,string? NextBeforeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateMonthlyCashflow(string OperationId,string PositionId);
public sealed record MonthlyCashflowCalculation(string CalculationId,string OperationId,string CustomerId,
    ReviewedMonthlyCashflowPosition Position,string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,
    string State,string? TotalIncome,string? TotalExpenses,string? Balance,IReadOnlyList<string> Issues,
    string RecordedBy,DateTimeOffset RecordedAt);
public sealed record MonthlyCashflowCalculationPage(IReadOnlyList<MonthlyCashflowCalculation> Items,bool HasMore,string? NextBeforeId);

// Wire-format and identity checks. Financial Rules alone evaluates the formula.
public static class MonthlyCashflowData
{
    public const int MaximumLines=200;
    public static bool Operation(string? value)=>value is {Length:32}&&Guid.TryParseExact(value,"N",out _)&&!value.Any(char.IsAsciiLetterUpper);
    public static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value==value.Trim()&&!value.Any(char.IsControl);
    public static bool Currency(string? value)=>value is {Length:3}&&value.All(char.IsAsciiLetterUpper);
    public static bool Money(string? value)=>value is {Length:>=4 and <=15}&&value[^3]=='.'&&
        value.Take(value.Length-3).All(char.IsAsciiDigit)&&value.TakeLast(2).All(char.IsAsciiDigit)&&
        (value.Length==4||value[0]!='0')&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount is >=0 and <=999999999999.99m;
    public static bool Date(DateOnly date,DateTimeOffset now)=>date.Year>=1900&&date<=DateOnly.FromDateTime(now.UtcDateTime.AddHours(14));
    public static bool Line(MonthlyCashflowLineDraft? line,DateTimeOffset now)=>line is not null&&Operation(line.LineId)&&Text(line.Name,120)&&Text(line.SourceNote,500)&&
        line.Side is "INCOME" or "EXPENSE"&&Text(line.Kind,80)&&Text(line.Member,80)&&
        Currency(line.Currency)&&(line.Amount is null||Money(line.Amount))&&(line.ValueDate is null||Date(line.ValueDate.Value,now))&&
        (line.Record is null||Text(line.Record.RecordId,80)&&line.Record.Revision>0&&line.Record.Kind==line.Side);
    public static bool Draft(MonthlyCashflowPositionDraft? value,DateTimeOffset now)=>value is {Lines:not null}&&Date(value.AsOfDate,now)&&value.AsOfDate<DateOnly.FromDateTime(now.UtcDateTime)&&value.AsOfDate.Day==DateTime.DaysInMonth(value.AsOfDate.Year,value.AsOfDate.Month)&&Text(value.Household,120)&&Currency(value.Currency)&&
        value.Lines.Count<=MaximumLines&&value.Lines.All(line=>Line(line,now))&&value.Lines.Select(x=>x.LineId).Distinct(StringComparer.Ordinal).Count()==value.Lines.Count&&
        value.Lines.Where(x=>x.Record is not null).Select(x=>x.Record!.Kind+"|"+x.Record.RecordId).Distinct(StringComparer.Ordinal).Count()==value.Lines.Count(x=>x.Record is not null)&&
        (value.ReplacesPositionId is null||Text(value.ReplacesPositionId,80));
    public static bool Bound(ReviewedMonthlyCashflowPosition? value,string customer,DateTimeOffset now)=>value is {Lines:not null}&&value.CustomerId==customer&&Text(value.PositionId,80)&&Operation(value.OperationId)&&Text(value.RecordedBy,160)&&value.RecordedAt!=default&&Draft(value.Position,now)&&
        value.Lines.Count==value.Position.Lines.Count&&value.Lines.Select(x=>x?.Line).SequenceEqual(value.Position.Lines)&&value.Lines.All(x=>x is not null&&
        (x.Line.Record is null?x.Source is null:x.Source is not null&&Text(x.Source.FinancialFactId,160)&&x.Source.SourceRevision>0&&Text(x.Source.ProvenanceId,160)&&Text(x.Source.DocumentVersionId,160)&&Text(x.Source.EvidenceId,160)&&Text(x.Source.ReviewedBy,160)&&x.Source.ReviewedAt!=default));
    public static bool Calculation(MonthlyCashflowCalculation? value,string customer)=>value is not null&&value.CustomerId==customer&&Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&MonthlyCashflowData.Bound(value.Position,customer,DateTimeOffset.UtcNow)&&
        value.RuleId==MonthlyCashflowDefinition.Id&&value.RuleVersion==MonthlyCashflowDefinition.Version&&value.DefinitionHash==MonthlyCashflowDefinition.DefinitionHash&&value.NumericSemantics==MonthlyCashflowDefinition.Semantics&&value.Issues is not null&&
        (value.State=="CALCULATED"&&value.Position.Position.IncomeComplete&&value.Position.Position.ExpensesComplete&&value.Issues.Count==0&&Total(value.TotalIncome,false)&&Total(value.TotalExpenses,false)&&Total(value.Balance,true)||value.State=="INSUFFICIENT_DATA"&&value.TotalIncome is null&&value.TotalExpenses is null&&value.Balance is null&&value.Issues.Count>0&&value.Issues.Count<=8&&value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "INCOME_INVENTORY_NOT_CONFIRMED" or "EXPENSE_INVENTORY_NOT_CONFIRMED" or "MISSING_VALUE" or "MISSING_VALUE_DATE" or "MIXED_VALUE_DATES" or "MIXED_CURRENCIES" or "OVERLAPPING_SOURCE_RECORDS" or "DUPLICATE_REVIEW_LINES"));
    public static bool Total(string? value,bool allowNegative)=>value is not null&&value.Length<=20&&decimal.TryParse(value,allowNegative?NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign:NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount.ToString("F2",CultureInfo.InvariantCulture)==value&&(allowNegative||amount>=0);
    public static bool Same<T>(T left,T right)=>JsonSerializer.Serialize(left,ContractJson.Options)==JsonSerializer.Serialize(right,ContractJson.Options);
}

public static class MonthlyCashflowDefinition
{
    public const string Id="monergy.monthly-cashflow";
    public const string Version="1.0.0";
    public const string Semantics="Sum reviewed whole-calendar-month income and expenses separately; balance equals income minus expenses; exact checked decimal cents; one completed month, household scope and currency; no FX, proration, annualisation or carry-forward; complete customer-reviewed inventories required. Comparisons subtract earlier totals from later totals for the same household and currency.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompareMonthlyCashflow(string EarlierCalculationId,string LaterCalculationId);
public sealed record MonthlyCashflowComparison(MonthlyCashflowCalculation Earlier,MonthlyCashflowCalculation Later,string IncomeChange,string ExpenseChange,string BalanceChange);
