using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialRules.Domain;
public sealed record MonthlyCashflowEvaluation(string State,string? TotalIncome,string? TotalExpenses,string? Balance,IReadOnlyList<string> Issues);
public sealed class MonthlyCashflowRule
{
    public const string Id=MonthlyCashflowDefinition.Id;
    public const string Version=MonthlyCashflowDefinition.Version;
    public const string Semantics=MonthlyCashflowDefinition.Semantics;
    public static string DefinitionHash=>MonthlyCashflowDefinition.DefinitionHash;
    public MonthlyCashflowRule(IConfiguration config)
    {
        if((config["Monergy:MonthlyCashflow:RuleVersion"]??Version)!=Version)
            throw new InvalidOperationException("The configured monthly cashflow rule version is unavailable.");
    }
    public MonthlyCashflowEvaluation Evaluate(ReviewedMonthlyCashflowPosition position)
    {
        if(!MonthlyCashflowData.Bound(position,position.CustomerId,DateTimeOffset.UtcNow))throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_INVALID",503);
        var issues=new List<string>();var input=position.Position;
        if(!input.IncomeComplete)issues.Add("INCOME_INVENTORY_NOT_CONFIRMED");
        if(!input.ExpensesComplete)issues.Add("EXPENSE_INVENTORY_NOT_CONFIRMED");
        if(input.Lines.Any(x=>x.Amount is null))issues.Add("MISSING_VALUE");
        if(input.Lines.Any(x=>x.ValueDate is null))issues.Add("MISSING_VALUE_DATE");
        if(input.Lines.Any(x=>x.ValueDate is not null&&x.ValueDate!=input.AsOfDate))issues.Add("MIXED_VALUE_DATES");
        if(input.Lines.Any(x=>x.Currency!=input.Currency))issues.Add("MIXED_CURRENCIES");
        var sources=position.Lines.Where(x=>x.Source is not null).Select(x=>x.Source!.FinancialFactId).ToArray();
        if(sources.Distinct(StringComparer.Ordinal).Count()!=sources.Length)issues.Add("OVERLAPPING_SOURCE_RECORDS");
        if(input.Lines.GroupBy(x=>string.Join('|',x.Side,x.Member.ToUpperInvariant(),x.Kind.ToUpperInvariant(),x.Name.ToUpperInvariant())).Any(x=>x.Count()>1))issues.Add("DUPLICATE_REVIEW_LINES");
        if(issues.Count>0)return new("INSUFFICIENT_DATA",null,null,null,issues);
        decimal income=0,expenses=0;
        foreach(var line in input.Lines)
        {var value=decimal.Parse(line.Amount!,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);if(line.Side=="INCOME")income=checked(income+value);else expenses=checked(expenses+value);}
        return new("CALCULATED",Money(income),Money(expenses),Money(checked(income-expenses)),Array.Empty<string>());
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
