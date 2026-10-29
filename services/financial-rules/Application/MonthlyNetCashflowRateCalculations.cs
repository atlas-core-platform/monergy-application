using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Domain;
namespace Monergy.Services.FinancialRules.Application;

public interface IMonthlyNetCashflowRateCalculationRepository
{
    Task<MonthlyNetCashflowRateCalculation> CreateAsync(string tenant,string customer,string actor,string fingerprint,MonthlyNetCashflowRateCalculation value,CancellationToken ct);
    Task<MonthlyNetCashflowRateCalculation?> ReadAsync(string tenant,string customer,string id,CancellationToken ct);
    Task<MonthlyNetCashflowRateCalculationPage> ListAsync(string tenant,string customer,string? beforeId,CancellationToken ct);
}
public sealed class MonthlyNetCashflowRateCalculationsApplication(MonthlyCashflowCalculationsApplication cashflow,
    IMonthlyNetCashflowRateCalculationRepository repository,MonthlyNetCashflowRateRule rule,TimeProvider clock)
{
    public async Task<MonthlyNetCashflowRateCalculation> CalculateAsync(string tenant,string session,string customer,string actor,CalculateMonthlyNetCashflowRate input,CancellationToken ct)
    {
        if(!MonthlyNetCashflowRateData.Request(input))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_INVALID",400);
        var source=await cashflow.ReadAsync(tenant,session,customer,input.MonthlyCashflowCalculationId,ct).ConfigureAwait(false);var evaluation=rule.Evaluate(source);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input,ContractJson.Options)));
        var id="cashflow-rate-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,$"{tenant}|{customer}|{actor}|{input.OperationId}")))).ToLowerInvariant();
        var value=new MonthlyNetCashflowRateCalculation(id,input.OperationId,customer,source,MonthlyNetCashflowRateDefinition.Id,MonthlyNetCashflowRateDefinition.Version,
            MonthlyNetCashflowRateDefinition.DefinitionHash,MonthlyNetCashflowRateDefinition.Semantics,evaluation.State,evaluation.MonthlyIncome,evaluation.MonthlyExpenses,
            evaluation.NetCashflow,evaluation.RatePercent,evaluation.Issues,actor,clock.GetUtcNow());
        var receipt=await repository.CreateAsync(tenant,customer,actor,fingerprint,value,ct).ConfigureAwait(false);
        await RevalidateAsync(tenant,session,customer,receipt,ct).ConfigureAwait(false);return receipt;
    }
    public async Task<MonthlyNetCashflowRateCalculation> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_INVALID",400);
        var value=await repository.ReadAsync(tenant,customer,id,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_NOT_FOUND",404);
        await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return value;
    }
    public async Task<MonthlyNetCashflowRateCalculationPage> ListAsync(string tenant,string session,string customer,string? beforeId,CancellationToken ct)
    {
        if(beforeId is not null&&!TenantAccessClient.Identifier(beforeId,80))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_INVALID",400);
        var page=await repository.ListAsync(tenant,customer,beforeId,ct).ConfigureAwait(false);
        foreach(var value in page.Items)await RevalidateAsync(tenant,session,customer,value,ct).ConfigureAwait(false);return page;
    }
    private async Task RevalidateAsync(string tenant,string session,string customer,MonthlyNetCashflowRateCalculation value,CancellationToken ct)
    {
        if(!MonthlyNetCashflowRateData.Calculation(value,customer))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_RESPONSE_INVALID",503);
        var current=await cashflow.ReadAsync(tenant,session,customer,value.SourceMonthlyCashflow.CalculationId,ct).ConfigureAwait(false);
        if(!MonthlyCashflowData.Same(current,value.SourceMonthlyCashflow))throw new TenantBoundaryException("MONTHLY_NET_CASHFLOW_RATE_SOURCE_CHANGED",503);
    }
}
