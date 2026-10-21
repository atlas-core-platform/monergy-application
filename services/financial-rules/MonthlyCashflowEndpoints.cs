using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
namespace Monergy.Services.FinancialRules;
public static class MonthlyCashflowEndpoints
{
    public static void MapMonthlyCashflowCalculations(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/local/v1/customers/{customerId}/monthly-cashflow/calculations");group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapPost("/comparison",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowCalculationsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CompareMonthlyCashflow>(http).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.CompareAsync(tenant,session,customerId,input,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
        group.MapPost("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowCalculationsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateMonthlyCashflow>(http).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.CalculateAsync(tenant,session,customerId,before.ActorId,input,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
        group.MapGet("/{calculationId}",async(string customerId,string calculationId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowCalculationsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.ReadAsync(tenant,session,customerId,calculationId,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
        group.MapGet("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowCalculationsApplication owner)=>
        {
            var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.ListAsync(tenant,session,customerId,CustomerReviewHttp.Cursor(http.Request),http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
    }
}
