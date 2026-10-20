using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;
public interface INetWorthPositionReader
{
    Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct);
}
public interface INetWorthCalculationRepository
{
    Task<NetWorthCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,NetWorthCalculation value,CancellationToken ct);
    Task<NetWorthCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<NetWorthCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class NetWorthCalculationsApplication(INetWorthPositionReader positions,INetWorthCalculationRepository repository,NetWorthRule rule,TimeProvider clock)
{
    public async Task<NetWorthCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateNetWorth input,CancellationToken ct)
    {
        if(input is null||!NetWorthData.Operation(input.OperationId)||!TenantAccessClient.Identifier(input.PositionId,80))throw new TenantBoundaryException("NET_WORTH_INVALID",400);
        var position=await positions.ReadAsync(tenant,session,customer,input.PositionId,ct).ConfigureAwait(false);
        if(!NetWorthData.Bound(position,customer,clock.GetUtcNow())||position.PositionId!=input.PositionId)throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",503);
        var value=rule.Evaluate(position);var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="networth-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,new(id,input.OperationId,customer,position,NetWorthRule.Id,NetWorthRule.Version,NetWorthRule.DefinitionHash,NetWorthRule.Semantics,value.State,value.TotalAssets,value.TotalLiabilities,value.NetWorth,value.Issues,actor,clock.GetUtcNow()),ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<NetWorthCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("NET_WORTH_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("NET_WORTH_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<NetWorthCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("NET_WORTH_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,NetWorthCalculation value,CancellationToken ct)
    {
        var current=await positions.ReadAsync(tenant,session,customer,value.Position.PositionId,ct).ConfigureAwait(false);
        if(!NetWorthData.Bound(current,customer,clock.GetUtcNow())||!NetWorthData.Same(current,value.Position))throw new TenantBoundaryException("NET_WORTH_SOURCE_CHANGED",503);
    }
}
