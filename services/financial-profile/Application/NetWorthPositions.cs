using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.FinancialProfile.Application;
public interface INetWorthPositionRepository
{
    Task<ReviewedNetWorthPosition> CreateAsync(string tenant,string customer,string actor,string fingerprint,ReviewedNetWorthPosition value,CancellationToken ct);
    Task<ReviewedNetWorthPosition?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<NetWorthPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class NetWorthPositionsApplication(INetWorthPositionRepository repository,IInvestmentRecordRepository investments,
    IPropertyRecordRepository properties,IVehicleRecordRepository vehicles,ILiabilityRecordRepository liabilities,TimeProvider clock)
{
    public async Task<ReviewedNetWorthPosition> CreateAsync(string tenant,string customer,string actor,CreateNetWorthPosition input,CancellationToken ct)
    {
        if(input is null||!NetWorthData.Operation(input.OperationId)||!NetWorthData.Draft(input.Position,clock.GetUtcNow()))throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",400);
        if(input.Position.ReplacesPositionId is {} previous)await ReadAsync(tenant,customer,previous,ct).ConfigureAwait(false);
        var lines=new List<NetWorthPositionLine>();
        foreach(var line in input.Position.Lines)lines.Add(await ResolveLineAsync(tenant,customer,line,ct).ConfigureAwait(false));
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="position-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        return await repository.CreateAsync(tenant,customer,actor,fingerprint,new(id,input.OperationId,customer,input.Position,lines,actor,clock.GetUtcNow()),ct).ConfigureAwait(false);
    }
    public async Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",400);
        return await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("NET_WORTH_POSITION_NOT_FOUND",404);
    }
    public Task<NetWorthPositionPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",400);
        return repository.ListAsync(tenant,customer,beforeId,ct);
    }
    private async Task<NetWorthPositionLine> ResolveLineAsync(string tenant,string customer,NetWorthLineDraft line,CancellationToken ct)
    {
        if(line.Record is not {} reference)return new(line,null);
        string? name=null,currency=null,amount=null;DateOnly? date=null;NetWorthLineSource? source=null;
        switch(reference.Kind)
        {
            case "INVESTMENT":
                var investment=await investments.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
                if(investment is not null&&investment.CustomerId==customer&&investment.Revision==reference.Revision&&investment.InvestmentRecordId==reference.RecordId)
                {name=investment.Investment.Name;currency=investment.Currency;amount=Money(investment.Investment.CurrentValue);date=investment.Investment.ValueAsOfDate;source=new(investment.Investment.SourceFinancialFactId,investment.Investment.SourceRevision,investment.SourceProvenanceId,investment.DocumentVersionId,investment.EvidenceId,investment.RecordedBy,investment.RecordedAt);}break;
            case "PROPERTY":
                var property=await properties.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
                if(property is not null&&property.CustomerId==customer&&property.Revision==reference.Revision&&property.PropertyRecordId==reference.RecordId)
                {name=property.Property.Name;currency=property.Currency;amount=Money(property.Property.CurrentValue);date=property.Property.ValueAsOfDate;source=new(property.Property.SourceFinancialFactId,property.Property.SourceRevision,property.SourceProvenanceId,property.DocumentVersionId,property.EvidenceId,property.RecordedBy,property.RecordedAt);}break;
            case "VEHICLE":
                var vehicle=await vehicles.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
                if(vehicle is not null&&vehicle.CustomerId==customer&&vehicle.Revision==reference.Revision&&vehicle.VehicleRecordId==reference.RecordId)
                {name=vehicle.Vehicle.Name;currency=vehicle.Currency;amount=Money(vehicle.Vehicle.CurrentValue);date=vehicle.Vehicle.ValueAsOfDate;source=new(vehicle.Vehicle.SourceFinancialFactId,vehicle.Vehicle.SourceRevision,vehicle.SourceProvenanceId,vehicle.DocumentVersionId,vehicle.EvidenceId,vehicle.RecordedBy,vehicle.RecordedAt);}break;
            case "LIABILITY":
                var liability=await liabilities.ReadAsync(tenant,customer,reference.RecordId,ct,reference.Revision).ConfigureAwait(false);
                if(liability is {VerificationStatus:"CUSTOMER_REVIEWED"}&&liability.CustomerId==customer&&liability.Revision==reference.Revision&&liability.LiabilityRecordId==reference.RecordId)
                {name=liability.Liability.Name;currency=liability.Currency;amount=Money(liability.Liability.OutstandingAmount);date=liability.Liability.BalanceAsOfDate;source=new(liability.Liability.SourceFinancialFactId,liability.Liability.SourceRevision,liability.SourceProvenanceId,liability.DocumentVersionId,liability.EvidenceId,liability.RecordedBy,liability.RecordedAt);}break;
        }
        if(source is null)throw new TenantBoundaryException("NET_WORTH_SOURCE_NOT_FOUND",404);
        if(line.Name!=name||line.Currency!=currency||line.Amount!=amount||line.ValueDate!=date)throw new TenantBoundaryException("NET_WORTH_SOURCE_MISMATCH",409);
        return new(line,source);
    }
    private static string Money(decimal value)=>value.ToString("F2",CultureInfo.InvariantCulture);
}
