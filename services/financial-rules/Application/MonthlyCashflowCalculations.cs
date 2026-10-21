using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;
public interface IMonthlyCashflowPositionReader
{
    Task<ReviewedMonthlyCashflowPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct);
}
public interface IMonthlyCashflowCalculationRepository
{
    Task<MonthlyCashflowCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,MonthlyCashflowCalculation value,CancellationToken ct);
    Task<MonthlyCashflowCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<MonthlyCashflowCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class MonthlyCashflowCalculationsApplication(IMonthlyCashflowPositionReader positions,IMonthlyCashflowCalculationRepository repository,MonthlyCashflowRule rule,TimeProvider clock)
{
    public async Task<MonthlyCashflowCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateMonthlyCashflow input,CancellationToken ct)
    {
        if(input is null||!MonthlyCashflowData.Operation(input.OperationId)||!TenantAccessClient.Identifier(input.PositionId,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);
        var position=await positions.ReadAsync(tenant,session,customer,input.PositionId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Bound(position,customer,clock.GetUtcNow())||position.PositionId!=input.PositionId)throw new TenantBoundaryException("MONTHLY_CASHFLOW_POSITION_INVALID",503);
        var value=rule.Evaluate(position);var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="cashflow-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,new(id,input.OperationId,customer,position,MonthlyCashflowRule.Id,MonthlyCashflowRule.Version,MonthlyCashflowRule.DefinitionHash,MonthlyCashflowRule.Semantics,value.State,value.TotalIncome,value.TotalExpenses,value.Balance,value.Issues,actor,clock.GetUtcNow()),ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<MonthlyCashflowCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_CASHFLOW_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<MonthlyCashflowCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("MONTHLY_CASHFLOW_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    public async Task<MonthlyCashflowComparison> CompareAsync(string tenant,string session,string customer,CompareMonthlyCashflow input,CancellationToken ct)
    {
        var earlier=await ReadAsync(tenant,session,customer,input.EarlierCalculationId,ct).ConfigureAwait(false);
        var later=await ReadAsync(tenant,session,customer,input.LaterCalculationId,ct).ConfigureAwait(false);
        if(earlier.State!="CALCULATED"||later.State!="CALCULATED"||earlier.Position.Position.AsOfDate>=later.Position.Position.AsOfDate||
            earlier.Position.Position.Currency!=later.Position.Position.Currency||earlier.Position.Position.Household!=later.Position.Position.Household)
            throw new TenantBoundaryException("MONTHLY_CASHFLOW_COMPARISON_INVALID",409);
        static string Difference(string later,string earlier)=>(decimal.Parse(later,CultureInfo.InvariantCulture)-decimal.Parse(earlier,CultureInfo.InvariantCulture)).ToString("F2",CultureInfo.InvariantCulture);
        return new(earlier,later,Difference(later.TotalIncome!,earlier.TotalIncome!),Difference(later.TotalExpenses!,earlier.TotalExpenses!),Difference(later.Balance!,earlier.Balance!));
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,MonthlyCashflowCalculation value,CancellationToken ct)
    {
        var current=await positions.ReadAsync(tenant,session,customer,value.Position.PositionId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Bound(current,customer,clock.GetUtcNow())||!MonthlyCashflowData.Same(current,value.Position))throw new TenantBoundaryException("MONTHLY_CASHFLOW_SOURCE_CHANGED",503);
    }
}
