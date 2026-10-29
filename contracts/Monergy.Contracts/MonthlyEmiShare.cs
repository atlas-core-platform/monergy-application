using System.Globalization;
using System.Text.Json.Serialization;
namespace Monergy.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonthlyEmiLoanReference(string LoanRecordId,int Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalculateMonthlyEmiShare(string OperationId,string MonthlyCashflowCalculationId,
    IReadOnlyList<MonthlyEmiLoanReference> Loans,bool ActiveLoansComplete);
public sealed record MonthlyEmiShareCalculation(string CalculationId,string OperationId,string CustomerId,
    MonthlyCashflowCalculation SourceMonthlyCashflow,IReadOnlyList<ReviewedLoanRecord> SourceLoans,bool ActiveLoansComplete,
    string RuleId,string RuleVersion,string DefinitionHash,string NumericSemantics,string State,
    string? MonthlyIncome,string? MonthlyEmi,string? RatePercent,IReadOnlyList<string> Issues,string RecordedBy,DateTimeOffset RecordedAt);
public sealed record MonthlyEmiShareCalculationPage(IReadOnlyList<MonthlyEmiShareCalculation> Items,bool HasMore,string? NextBeforeId);

public static class MonthlyEmiShareDefinition
{
    public const string Id="monergy.customer-reviewed-monthly-emi-share";
    public const string Version="1.0.0";
    public const string Semantics="For one exact calculated customer-reviewed completed Monthly Cashflow receipt and exact selected reviewed active Loan Record revisions, sum the recorded monthly EMI values, divide by reviewed monthly income and multiply by 100. Require customer confirmation that the selected records represent all active loan EMIs for this review. Use exact checked decimal arithmetic and round the percentage to two decimal places away from zero. The browser cannot post income, EMI totals or a rate. This is a customer-reviewed descriptive monthly EMI share, not independently verified debt, an affordability threshold, eligibility or underwriting decision, recommendation or advice.";
    public static string DefinitionHash{get;}=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Id+"\n"+Version+"\n"+Semantics))).ToLowerInvariant();
}

public static class MonthlyEmiShareData
{
    public const int MaximumLoans=200;
    public static bool Request(CalculateMonthlyEmiShare? value)=>value is {Loans:not null}&&MonthlyCashflowData.Operation(value.OperationId)&&
        MonthlyCashflowData.Text(value.MonthlyCashflowCalculationId,80)&&value.Loans.Count<=MaximumLoans&&
        value.Loans.All(x=>x is not null&&MonthlyCashflowData.Text(x.LoanRecordId,80)&&x.Revision>0)&&
        value.Loans.Select(x=>x.LoanRecordId+"|"+x.Revision.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count()==value.Loans.Count;
    public static bool Loan(ReviewedLoanRecord? value,string customer)=>value is {Loan:not null}&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.LoanRecordId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&value.Revision>0&&
        MonthlyCashflowData.Text(value.Loan.Name,120)&&MonthlyCashflowData.Text(value.Loan.Lender,120)&&MonthlyCashflowData.Text(value.Loan.LoanType,80)&&
        value.Loan.Emi>=0&&value.Loan.Emi<=999999999999.99m&&value.Loan.StartDate<=value.Loan.EndDate&&value.Loan.BalanceAsOfDate>=value.Loan.StartDate&&
        MonthlyCashflowData.Currency(value.Currency)&&MonthlyCashflowData.Text(value.SourceProvenanceId,160)&&MonthlyCashflowData.Text(value.DocumentVersionId,160)&&
        MonthlyCashflowData.Text(value.EvidenceId,160)&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&
        (value.LifecycleStatus=="UNREVIEWED"&&value.LifecycleAsOfDate is null||value.LifecycleStatus is "ACTIVE" or "CLOSED"&&value.LifecycleAsOfDate is not null);
    public static bool Calculation(MonthlyEmiShareCalculation? value,string customer)=>value is {SourceLoans:not null,Issues:not null}&&value.CustomerId==customer&&
        MonthlyCashflowData.Text(value.CalculationId,80)&&MonthlyCashflowData.Operation(value.OperationId)&&MonthlyCashflowData.Calculation(value.SourceMonthlyCashflow,customer)&&
        value.SourceLoans.Count<=MaximumLoans&&value.SourceLoans.All(x=>Loan(x,customer))&&value.SourceLoans.Select(x=>x.LoanRecordId+"|"+x.Revision.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count()==value.SourceLoans.Count&&
        value.RuleId==MonthlyEmiShareDefinition.Id&&value.RuleVersion==MonthlyEmiShareDefinition.Version&&value.DefinitionHash==MonthlyEmiShareDefinition.DefinitionHash&&
        value.NumericSemantics==MonthlyEmiShareDefinition.Semantics&&MonthlyCashflowData.Text(value.RecordedBy,160)&&value.RecordedAt!=default&&
        (value.State=="CALCULATED"&&value.ActiveLoansComplete&&value.SourceMonthlyCashflow.State=="CALCULATED"&&value.SourceLoans.Count>0&&value.Issues.Count==0&&Money(value.MonthlyIncome)&&Money(value.MonthlyEmi)&&Rate(value.RatePercent)&&value.MonthlyIncome==value.SourceMonthlyCashflow.TotalIncome||
         value.State=="INSUFFICIENT_DATA"&&value.MonthlyIncome is null&&value.MonthlyEmi is null&&value.RatePercent is null&&value.Issues.Count is >0 and <=5&&
         value.Issues.Distinct(StringComparer.Ordinal).Count()==value.Issues.Count&&value.Issues.All(x=>x is "MONTHLY_CASHFLOW_NOT_CALCULATED" or "ACTIVE_LOANS_NOT_CONFIRMED" or "NO_ACTIVE_LOANS_SELECTED" or "MONTHLY_INCOME_ZERO" or "LOAN_NOT_ACTIVE_FOR_MONTH" or "CURRENCY_MISMATCH"));
    private static bool Money(string? value)=>value is not null&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
    private static bool Rate(string? value)=>value is not null&&decimal.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)&&amount>=0&&amount.ToString("F2",CultureInfo.InvariantCulture)==value;
}
