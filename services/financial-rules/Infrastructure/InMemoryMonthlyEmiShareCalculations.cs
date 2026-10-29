using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
namespace Monergy.Services.FinancialRules.Infrastructure;
public sealed class InMemoryMonthlyEmiShareCalculations:IMonthlyEmiShareCalculationRepository
{
    private readonly object sync=new();private readonly Dictionary<(string Tenant,string Customer,string Id),(string Fingerprint,MonthlyEmiShareCalculation Value)> records=[];
    public Task<MonthlyEmiShareCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,MonthlyEmiShareCalculation value,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();lock(sync){var key=(tenant,customer,value.CalculationId);if(records.TryGetValue(key,out var prior)){if(prior.Fingerprint!=fingerprint||prior.Value.RecordedBy!=actor)throw new TenantBoundaryException("MONTHLY_EMI_SHARE_OPERATION_CONFLICT",409);return Task.FromResult(Clone(prior.Value));}records[key]=(fingerprint,Clone(value));return Task.FromResult(Clone(value));}}
    public Task<MonthlyEmiShareCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();lock(sync)return Task.FromResult(records.TryGetValue((tenant,customer,id),out var value)?Clone(value.Value):null);}
    public Task<MonthlyEmiShareCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();lock(sync){var all=records.Where(x=>x.Key.Tenant==tenant&&x.Key.Customer==customer).Select(x=>x.Value.Value).OrderByDescending(x=>x.RecordedAt).ThenByDescending(x=>x.CalculationId,StringComparer.Ordinal).ToArray();var index=beforeId is null?-1:Array.FindIndex(all,x=>x.CalculationId==beforeId);if(beforeId is not null&&index<0)throw new TenantBoundaryException("MONTHLY_EMI_SHARE_NOT_FOUND",404);var items=all.Skip(index+1).Take(51).ToArray();return Task.FromResult(new MonthlyEmiShareCalculationPage(items.Take(50).Select(Clone).ToArray(),items.Length>50,items.Length>50?items[49].CalculationId:null));}}
    private static MonthlyEmiShareCalculation Clone(MonthlyEmiShareCalculation value)=>JsonSerializer.Deserialize<MonthlyEmiShareCalculation>(JsonSerializer.Serialize(value,ContractJson.Options),ContractJson.Options)!;
}
