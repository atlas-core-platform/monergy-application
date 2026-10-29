using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IDebtServiceRatioCalculationRepository
{
    Task<DebtServiceRatioCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,DebtServiceRatioCalculation value,CancellationToken ct);
    Task<DebtServiceRatioCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<DebtServiceRatioCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class DebtServiceRatioCalculationsApplication(MonthlyCashflowCalculationsApplication cashflow,IDebtServiceRatioCalculationRepository repository,DebtServiceRatioRule rule,TimeProvider clock)
{
    public async Task<DebtServiceRatioCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateDebtServiceRatio input,CancellationToken ct)
    {
        if(!DebtServiceRatioData.Request(input))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_INVALID",400);
        var source=await cashflow.ReadAsync(tenant,session,customer,input.MonthlyCashflowCalculationId,ct).ConfigureAwait(false);
        var evaluation=rule.Evaluate(source,input.DebtPaymentLineIds,input.DebtPaymentsComplete);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="debt-ratio-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new DebtServiceRatioCalculation(id,input.OperationId,customer,source,input.DebtPaymentLineIds.ToArray(),input.DebtPaymentsComplete,
            DebtServiceRatioDefinition.Id,DebtServiceRatioDefinition.Version,DebtServiceRatioDefinition.DefinitionHash,DebtServiceRatioDefinition.Semantics,
            evaluation.State,evaluation.MonthlyIncome,evaluation.MonthlyDebtPayments,evaluation.RatioPercent,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<DebtServiceRatioCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("DEBT_SERVICE_RATIO_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<DebtServiceRatioCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,DebtServiceRatioCalculation value,CancellationToken ct)
    {
        if(!DebtServiceRatioData.Calculation(value,customer))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_RESPONSE_INVALID",503);
        var current=await cashflow.ReadAsync(tenant,session,customer,value.SourceCalculation.CalculationId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Same(current,value.SourceCalculation))throw new TenantBoundaryException("DEBT_SERVICE_RATIO_SOURCE_CHANGED",503);
    }
}
