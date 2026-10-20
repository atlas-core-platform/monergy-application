using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile.Infrastructure;
public sealed class InMemoryNetWorthPositions:INetWorthPositionRepository
{
    private readonly object sync=new();
    private readonly Dictionary<(string Tenant,string Customer,string Id),(string Fingerprint,ReviewedNetWorthPosition Value)> records=[];
    public Task<ReviewedNetWorthPosition> CreateAsync(string tenant,string customer,string actor,string fingerprint,ReviewedNetWorthPosition value,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(sync)
        {
            var key=(tenant,customer,value.PositionId);
            if(records.TryGetValue(key,out var prior))
            {if(prior.Fingerprint!=fingerprint||prior.Value.RecordedBy!=actor)throw new TenantBoundaryException("NET_WORTH_OPERATION_CONFLICT",409);return Task.FromResult(Clone(prior.Value));}
            records.Add(key,(fingerprint,Clone(value)));return Task.FromResult(Clone(value));
        }
    }
    public Task<ReviewedNetWorthPosition?> ReadAsync(string tenant,string customer,string id,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();lock(sync)return Task.FromResult(records.TryGetValue((tenant,customer,id),out var item)?Clone(item.Value):null);}
    public Task<NetWorthPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(sync)
        {
            var all=records.Where(x=>x.Key.Tenant==tenant&&x.Key.Customer==customer).Select(x=>x.Value.Value).OrderByDescending(x=>x.RecordedAt).ThenByDescending(x=>x.PositionId,StringComparer.Ordinal).ToArray();
            var index=beforeId is null?-1:Array.FindIndex(all,x=>x.PositionId==beforeId);
            if(beforeId is not null&&index<0)throw new TenantBoundaryException("NET_WORTH_POSITION_NOT_FOUND",404);
            var items=all.Skip(index+1).Take(51).ToArray();return Task.FromResult(new NetWorthPositionPage(items.Take(50).Select(Clone).ToArray(),items.Length>50,items.Length>50?items[49].PositionId:null));
        }
    }
    private static ReviewedNetWorthPosition Clone(ReviewedNetWorthPosition value)=>JsonSerializer.Deserialize<ReviewedNetWorthPosition>(JsonSerializer.Serialize(value,ContractJson.Options),ContractJson.Options)!;
}
