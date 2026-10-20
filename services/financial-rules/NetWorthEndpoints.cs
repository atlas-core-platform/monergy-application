using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
namespace Monergy.Services.FinancialRules;
public static class NetWorthEndpoints
{
    public static void MapNetWorthCalculations(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/local/v1/customers/{customerId}/net-worth/calculations");group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapPost("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,NetWorthCalculationsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateNetWorth>(http).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.CalculateAsync(tenant,session,customerId,before.ActorId,input,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
        group.MapGet("/{calculationId}",async(string customerId,string calculationId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,NetWorthCalculationsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.ReadAsync(tenant,session,customerId,calculationId,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
        group.MapGet("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,NetWorthCalculationsApplication owner)=>
        {
            var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false);var(tenant,session)=TenantAccessClient.Headers(http);
            var value=await owner.ListAsync(tenant,session,customerId,CustomerReviewHttp.Cursor(http.Request),http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,calculate:true).ConfigureAwait(false));return value;
        });
    }
}
