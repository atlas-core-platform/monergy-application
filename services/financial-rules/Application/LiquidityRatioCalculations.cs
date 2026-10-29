using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface ILiquidityRatioCalculationRepository
{
    Task<LiquidityRatioCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,LiquidityRatioCalculation value,CancellationToken ct);
    Task<LiquidityRatioCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<LiquidityRatioCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class LiquidityRatioCalculationsApplication(NetWorthCalculationsApplication netWorth,MonthlyCashflowCalculationsApplication cashflow,
    ILiquidityRatioCalculationRepository repository,LiquidityRatioRule rule,TimeProvider clock)
{
    public async Task<LiquidityRatioCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateLiquidityRatio input,CancellationToken ct)
    {
        if(!LiquidityRatioData.Request(input))throw new TenantBoundaryException("LIQUIDITY_RATIO_INVALID",400);
        var net=await netWorth.ReadAsync(tenant,session,customer,input.NetWorthCalculationId,ct).ConfigureAwait(false);
        var monthly=await cashflow.ReadAsync(tenant,session,customer,input.MonthlyCashflowCalculationId,ct).ConfigureAwait(false);
        var evaluation=rule.Evaluate(net,monthly,input.LiquidAssetLineIds,input.LiquidAssetsComplete);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="liquidity-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new LiquidityRatioCalculation(id,input.OperationId,customer,net,monthly,input.LiquidAssetLineIds.ToArray(),input.LiquidAssetsComplete,
            LiquidityRatioDefinition.Id,LiquidityRatioDefinition.Version,LiquidityRatioDefinition.DefinitionHash,LiquidityRatioDefinition.Semantics,
            evaluation.State,evaluation.LiquidAssets,evaluation.MonthlyExpenses,evaluation.RatioMonths,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<LiquidityRatioCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("LIQUIDITY_RATIO_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("LIQUIDITY_RATIO_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<LiquidityRatioCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("LIQUIDITY_RATIO_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,LiquidityRatioCalculation value,CancellationToken ct)
    {
        if(!LiquidityRatioData.Calculation(value,customer))throw new TenantBoundaryException("LIQUIDITY_RATIO_RESPONSE_INVALID",503);
        var net=await netWorth.ReadAsync(tenant,session,customer,value.SourceNetWorth.CalculationId,ct).ConfigureAwait(false);
        var monthly=await cashflow.ReadAsync(tenant,session,customer,value.SourceMonthlyCashflow.CalculationId,ct).ConfigureAwait(false);
        if(!NetWorthData.Same(net,value.SourceNetWorth)||!MonthlyCashflowData.Same(monthly,value.SourceMonthlyCashflow))throw new TenantBoundaryException("LIQUIDITY_RATIO_SOURCE_CHANGED",503);
    }
}
