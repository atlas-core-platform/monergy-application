using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;
public sealed class AssetSnapshotApplication(IInvestmentRecordRepository investments, IPropertyRecordRepository properties,
    IVehicleRecordRepository vehicles, TimeProvider clock)
{
    public async Task<AssetRecordSnapshot> ReadAsync(string tenant,string customer,DateOnly date,string currency,CancellationToken ct)
    {
        if(date.Year<1900 || date>DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(14)) ||
            currency is null || currency.Length!=3 || !currency.All(char.IsAsciiLetterUpper))
            throw new TenantBoundaryException("ASSET_SNAPSHOT_INVALID",400);
        // Independent owner-local reads of current reviewed knowledge; no cross-kind historical reconstruction.
        var investment=await investments.SnapshotAsync(tenant,customer,date,currency,ct).ConfigureAwait(false);
        var property=await properties.SnapshotAsync(tenant,customer,date,currency,ct).ConfigureAwait(false);
        var vehicle=await vehicles.SnapshotAsync(tenant,customer,date,currency,ct).ConfigureAwait(false);
        return new(date,currency,investment,property,vehicle);
    }
}
