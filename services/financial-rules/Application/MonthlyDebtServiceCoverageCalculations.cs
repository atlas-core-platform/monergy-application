using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IMonthlyDebtServiceCoverageCalculationRepository
{
    Task<MonthlyDebtServiceCoverageCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,MonthlyDebtServiceCoverageCalculation value,CancellationToken ct);
    Task<MonthlyDebtServiceCoverageCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<MonthlyDebtServiceCoverageCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class MonthlyDebtServiceCoverageCalculationsApplication(MonthlyCashflowCalculationsApplication cashflow,MonthlyEmiShareCalculationsApplication emiShares,
    IMonthlyDebtServiceCoverageCalculationRepository repository,MonthlyDebtServiceCoverageRule rule,TimeProvider clock)
{
    public async Task<MonthlyDebtServiceCoverageCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateMonthlyDebtServiceCoverage input,CancellationToken ct)
    {
        if(!MonthlyDebtServiceCoverageData.Request(input))throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_INVALID",400);
        var source=await cashflow.ReadAsync(tenant,session,customer,input.MonthlyCashflowCalculationId,ct).ConfigureAwait(false);
        var emi=await emiShares.ReadAsync(tenant,session,customer,input.MonthlyEmiShareCalculationId,ct).ConfigureAwait(false);
        var evaluation=rule.Evaluate(source,emi,input.DebtServiceIncludedInExpenses);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="debt-coverage-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new MonthlyDebtServiceCoverageCalculation(id,input.OperationId,customer,source,emi,input.DebtServiceIncludedInExpenses,
            MonthlyDebtServiceCoverageDefinition.Id,MonthlyDebtServiceCoverageDefinition.Version,MonthlyDebtServiceCoverageDefinition.DefinitionHash,
            MonthlyDebtServiceCoverageDefinition.Semantics,evaluation.State,evaluation.MonthlyIncome,evaluation.MonthlyExpenses,evaluation.MonthlyEmi,
            evaluation.MonthlyCashflowBalance,evaluation.CashAvailableForDebtService,evaluation.CoverageMultiple,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<MonthlyDebtServiceCoverageCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_INVALID",400);var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_NOT_FOUND",404);await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;}
    public async Task<MonthlyDebtServiceCoverageCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_INVALID",400);var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;}
    private async Task RevalidateAsync(string tenant,string session,string customer,MonthlyDebtServiceCoverageCalculation value,CancellationToken ct)
    {
        if(!MonthlyDebtServiceCoverageData.Calculation(value,customer))throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_RESPONSE_INVALID",503);
        var currentCashflow=await cashflow.ReadAsync(tenant,session,customer,value.SourceMonthlyCashflow.CalculationId,ct).ConfigureAwait(false);
        var currentEmi=await emiShares.ReadAsync(tenant,session,customer,value.SourceMonthlyEmiShare.CalculationId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Same(currentCashflow,value.SourceMonthlyCashflow)||!MonthlyCashflowData.Same(currentEmi,value.SourceMonthlyEmiShare))
            throw new TenantBoundaryException("MONTHLY_DEBT_SERVICE_COVERAGE_SOURCE_CHANGED",503);
    }
}
