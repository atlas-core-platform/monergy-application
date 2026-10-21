using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialProfile.Application;
namespace Monergy.Services.FinancialProfile;
public static class MonthlyCashflowPositionEndpoints
{
    public static void MapMonthlyCashflowPositions(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/local/v1/customers/{customerId}/monthly-cashflow/positions");group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapPost("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowPositionsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);
            await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,write:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CreateMonthlyCashflowPosition>(http).ConfigureAwait(false);
            var documents=input.Position?.Lines?.Any(x=>x?.Record is not null)==true;
            var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,write:true,documents:documents).ConfigureAwait(false);
            var(tenant,_)=TenantAccessClient.Headers(http);
            var value=await owner.CreateAsync(tenant,customerId,before.ActorId,input,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,write:true,documents:documents).ConfigureAwait(false));return value;
        });
        group.MapGet("/{positionId}",async(string customerId,string positionId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowPositionsApplication owner)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings).ConfigureAwait(false);var(tenant,_)=TenantAccessClient.Headers(http);
            var value=await owner.ReadAsync(tenant,customerId,positionId,http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,documents:value.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false));return value;
        });
        group.MapGet("",async(string customerId,HttpContext http,TenantAccessClient access,TenantBoundaryOptions bindings,MonthlyCashflowPositionsApplication owner)=>
        {
            var before=await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings).ConfigureAwait(false);var(tenant,_)=TenantAccessClient.Headers(http);
            var value=await owner.ListAsync(tenant,customerId,CustomerReviewHttp.Cursor(http.Request),http.RequestAborted).ConfigureAwait(false);
            CustomerReviewHttp.Confirm(before,await CustomerReviewHttp.AuthorizeAsync(http,customerId,access,bindings,documents:value.Items.Any(x=>x.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false));return value;
        });
    }
}
