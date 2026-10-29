using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IAssetAllocationCalculationRepository
{
    Task<AssetAllocationCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,AssetAllocationCalculation value,CancellationToken ct);
    Task<AssetAllocationCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<AssetAllocationCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class AssetAllocationCalculationsApplication(NetWorthCalculationsApplication netWorth,
    IAssetAllocationCalculationRepository repository,AssetAllocationRule rule,TimeProvider clock)
{
    public async Task<AssetAllocationCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateAssetAllocation input,CancellationToken ct)
    {
        if(!AssetAllocationData.Request(input))throw new TenantBoundaryException("ASSET_ALLOCATION_INVALID",400);
        var source=await netWorth.ReadAsync(tenant,session,customer,input.NetWorthCalculationId,ct).ConfigureAwait(false);
        var evaluation=rule.Evaluate(source);var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="allocation-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new AssetAllocationCalculation(id,input.OperationId,customer,source,evaluation.Categories,AssetAllocationDefinition.Id,AssetAllocationDefinition.Version,
            AssetAllocationDefinition.DefinitionHash,AssetAllocationDefinition.Semantics,evaluation.State,evaluation.TotalAssets,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<AssetAllocationCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("ASSET_ALLOCATION_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("ASSET_ALLOCATION_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<AssetAllocationCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("ASSET_ALLOCATION_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,AssetAllocationCalculation value,CancellationToken ct)
    {
        if(!AssetAllocationData.Calculation(value,customer))throw new TenantBoundaryException("ASSET_ALLOCATION_RESPONSE_INVALID",503);
        var source=await netWorth.ReadAsync(tenant,session,customer,value.SourceNetWorth.CalculationId,ct).ConfigureAwait(false);
        if(!NetWorthData.Same(source,value.SourceNetWorth))throw new TenantBoundaryException("ASSET_ALLOCATION_SOURCE_CHANGED",503);
    }
}
