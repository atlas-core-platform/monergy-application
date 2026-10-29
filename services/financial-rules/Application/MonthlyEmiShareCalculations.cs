using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IMonthlyEmiLoanReader
{Task<ReviewedLoanRecord> ReadAsync(string tenant,string session,string customer,string id,int revision,CancellationToken ct);}
public interface IMonthlyEmiShareCalculationRepository
{
    Task<MonthlyEmiShareCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,MonthlyEmiShareCalculation value,CancellationToken ct);
    Task<MonthlyEmiShareCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<MonthlyEmiShareCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class MonthlyEmiShareCalculationsApplication(MonthlyCashflowCalculationsApplication cashflow,IMonthlyEmiLoanReader loanReader,
    IMonthlyEmiShareCalculationRepository repository,MonthlyEmiShareRule rule,TimeProvider clock)
{
    public async Task<MonthlyEmiShareCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateMonthlyEmiShare input,CancellationToken ct)
    {
        if(!MonthlyEmiShareData.Request(input))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_INVALID",400);
        var source=await cashflow.ReadAsync(tenant,session,customer,input.MonthlyCashflowCalculationId,ct).ConfigureAwait(false);
        var loans=new List<ReviewedLoanRecord>(input.Loans.Count);
        foreach(var reference in input.Loans)loans.Add(await loanReader.ReadAsync(tenant,session,customer,reference.LoanRecordId,reference.Revision,ct).ConfigureAwait(false));
        var evaluation=rule.Evaluate(source,loans,input.ActiveLoansComplete);
        var canonical=input with{Loans=input.Loans.OrderBy(x=>x.LoanRecordId,StringComparer.Ordinal).ThenBy(x=>x.Revision).ToArray()};
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical,ContractJson.Options)));
        var id="emi-share-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new MonthlyEmiShareCalculation(id,input.OperationId,customer,source,loans,input.ActiveLoansComplete,MonthlyEmiShareDefinition.Id,MonthlyEmiShareDefinition.Version,
            MonthlyEmiShareDefinition.DefinitionHash,MonthlyEmiShareDefinition.Semantics,evaluation.State,evaluation.MonthlyIncome,evaluation.MonthlyEmi,evaluation.RatePercent,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<MonthlyEmiShareCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_INVALID",400);var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_EMI_SHARE_NOT_FOUND",404);await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;}
    public async Task<MonthlyEmiShareCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_INVALID",400);var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;}
    private async Task RevalidateAsync(string tenant,string session,string customer,MonthlyEmiShareCalculation value,CancellationToken ct)
    {
        if(!MonthlyEmiShareData.Calculation(value,customer))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_RESPONSE_INVALID",503);
        var current=await cashflow.ReadAsync(tenant,session,customer,value.SourceMonthlyCashflow.CalculationId,ct).ConfigureAwait(false);if(!MonthlyCashflowData.Same(current,value.SourceMonthlyCashflow))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_SOURCE_CHANGED",503);
        foreach(var embedded in value.SourceLoans){var loan=await loanReader.ReadAsync(tenant,session,customer,embedded.LoanRecordId,embedded.Revision,ct).ConfigureAwait(false);if(!MonthlyCashflowData.Same(loan,embedded))throw new TenantBoundaryException("MONTHLY_EMI_SHARE_SOURCE_CHANGED",503);}
    }
}
