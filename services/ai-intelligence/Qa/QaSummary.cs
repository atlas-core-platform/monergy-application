using System.Globalization;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.Services.AiIntelligence.Qa;
public sealed partial class QaOwners
{
    public async Task<IReadOnlyList<QaClaim>> Summary(HttpContext http,QaCustomer customer,string netWorthCalculation,string cashflowCalculation)
    {
        if(!TenantAccessClient.Identifier(netWorthCalculation,80)||!TenantAccessClient.Identifier(cashflowCalculation,80))throw new TenantBoundaryException("AI_SUMMARY_INVALID",400);
        await RequireCalculation(http,customer);
        var net=await NetWorthResult(http,customer,netWorthCalculation);
        var cash=await Cashflow(http,customer,cashflowCalculation);
        if(!NetWorthData.Calculation(net,customer.Workspace.CustomerId)||net.CalculationId!=netWorthCalculation)throw Invalid();
        if(net.Position.Position.AsOfDate!=cash.Position.Position.AsOfDate||net.Position.Position.Currency!=cash.Position.Position.Currency||
            cash.Position.Position.Household!="Self"||cash.Position.Position.Lines.Any(x=>x.Member!="Self"))
            throw new TenantBoundaryException("AI_SUMMARY_SCOPE_MISMATCH",409);
        // Owner reads below revalidate both immutable results and their exact reviewed inputs.
        var balanceSheet=NetWorthClaims(net);
        var monthly=MonthlyClaims(cash);
        var claims=balanceSheet.Concat(monthly).ToArray();
        if(claims.Any(x=>x.Id.EndsWith("-incomplete",StringComparison.Ordinal)))return claims.Where(x=>x.Id.EndsWith("-incomplete",StringComparison.Ordinal)).ToArray();
        return claims;
    }
    private async Task<MonthlyCashflowCalculation> Cashflow(HttpContext http,QaCustomer customer,string id)
    {
        if(!TenantAccessClient.Identifier(id,80))throw Invalid();await RequireCalculation(http,customer);
        Uri Route(string owner,string suffix)=>new(Root(owner),"local/v1/customers/"+Uri.EscapeDataString(customer.Workspace.CustomerId)+"/monthly-cashflow/"+suffix);
        using var request=Message(http,Route("FinancialRulesUrl","calculations/"+Uri.EscapeDataString(id)),HttpMethod.Get);
        var value=await Read<MonthlyCashflowCalculation>(http,request);
        if(!MonthlyCashflowData.Calculation(value,customer.Workspace.CustomerId)||value.CalculationId!=id)throw Invalid();
        using var inputRequest=Message(http,Route("FinancialProfileUrl","positions/"+Uri.EscapeDataString(value.Position.PositionId)),HttpMethod.Get);
        var input=await Read<ReviewedMonthlyCashflowPosition>(http,inputRequest);
        if(!MonthlyCashflowData.Bound(input,customer.Workspace.CustomerId,DateTimeOffset.UtcNow)||!MonthlyCashflowData.Same(input,value.Position))throw Invalid();
        await RequireCalculation(http,customer);return value;
    }
    private async Task<IReadOnlyList<QaClaim>> CashflowClaims(HttpContext http,QaCustomer customer,string id)
        => MonthlyClaims(await Cashflow(http,customer,id));
    private static IReadOnlyList<QaClaim> MonthlyClaims(MonthlyCashflowCalculation result)
    {
        var position=result.Position.Position;
        var date=position.AsOfDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        QaClaim Claim(string code,string text)=>new("monthly-"+code,text,"MonthlyCalculation",result.Position.PositionId,1,result.DefinitionHash,"",null,date,result.CalculationId,result.RuleVersion);
        if(result.State!="CALCULATED")return [Claim("incomplete","Financial Rules could not calculate monthly totals. Missing or incompatible inputs: "+string.Join(", ",result.Issues))];
        string Text(string label,string? amount)=>$"Financial Rules calculated {label}: {amount} {position.Currency}, for the completed month ending {date}, household scope {position.Household}, from the selected customer-reviewed inventory. These values are not externally verified.";
        return [Claim("income",Text("total monthly income",result.TotalIncome)),Claim("expenses",Text("total monthly expenses",result.TotalExpenses)),Claim("balance",Text("monthly income minus expenses",result.Balance))];
    }
}
