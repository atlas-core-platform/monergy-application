using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IDebtToIncomeCalculationRepository
{
    Task<DebtToIncomeCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,DebtToIncomeCalculation value,CancellationToken ct);
    Task<DebtToIncomeCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<DebtToIncomeCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class DebtToIncomeCalculationsApplication(DebtServiceRatioCalculationsApplication debtService,IDebtToIncomeCalculationRepository repository,DebtToIncomeRule rule,TimeProvider clock)
{
    public async Task<DebtToIncomeCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateDebtToIncome input,CancellationToken ct)
    {
        if(!DebtToIncomeData.Request(input))throw new TenantBoundaryException("DEBT_TO_INCOME_INVALID",400);
        var source=await debtService.ReadAsync(tenant,session,customer,input.DebtServiceRatioCalculationId,ct).ConfigureAwait(false);
        var evaluation=rule.Evaluate(source,input.GrossIncomeConfirmed);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="dti-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new DebtToIncomeCalculation(id,input.OperationId,customer,source,input.GrossIncomeConfirmed,DebtToIncomeDefinition.TimeBasis,DebtToIncomeDefinition.IncomeBasis,
            DebtToIncomeDefinition.Id,DebtToIncomeDefinition.Version,DebtToIncomeDefinition.DefinitionHash,DebtToIncomeDefinition.Semantics,
            evaluation.State,evaluation.MonthlyDebtObligations,evaluation.GrossMonthlyIncome,evaluation.RatioPercent,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<DebtToIncomeCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("DEBT_TO_INCOME_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("DEBT_TO_INCOME_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<DebtToIncomeCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("DEBT_TO_INCOME_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,DebtToIncomeCalculation value,CancellationToken ct)
    {
        if(!DebtToIncomeData.Calculation(value,customer))throw new TenantBoundaryException("DEBT_TO_INCOME_RESPONSE_INVALID",503);
        var current=await debtService.ReadAsync(tenant,session,customer,value.SourceDebtServiceRatio.CalculationId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Same(current,value.SourceDebtServiceRatio))throw new TenantBoundaryException("DEBT_TO_INCOME_SOURCE_CHANGED",503);
    }
}
