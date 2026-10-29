using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
namespace Monergy.UatGateway;

public static class ProductNetWorthEndpoints
{
    public static void AddProductNetWorth(this WebApplicationBuilder builder)
    {if(builder.Configuration.GetValue<bool>("Monergy:Product:Enabled"))builder.Services.AddSingleton(provider=>new ProductNetWorthClient(provider.GetRequiredService<IConfiguration>()));}
    public static void MapProductNetWorth(this WebApplication app)
    {
        if(!app.Configuration.GetValue<bool>("Monergy:Product:Enabled"))return;
        var group=app.MapGroup("/product-api/v1/net-worth");group.AddEndpointFilter(CustomerReviewHttp.Filter);
        group.MapPost("/positions",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,write:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CreateNetWorthPosition>(http).ConfigureAwait(false);
            if(!NetWorthData.Operation(input.OperationId)||!NetWorthData.Draft(input.Position,DateTimeOffset.UtcNow))throw new TenantBoundaryException("NET_WORTH_INVALID",400);
            var receipt=await client.SavePosition(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Bound(receipt,customer.CustomerId,DateTimeOffset.UtcNow)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||!NetWorthData.Same(receipt.Position,input.Position))throw Invalid();
            var saved=await client.Position(session,customer.CustomerId,receipt.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,write:true,documents:receipt.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/positions",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.Positions(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>NetWorthData.Bound(x,customer.CustomerId,DateTimeOffset.UtcNow),x=>x.PositionId)||page.Items.Any(x=>x.PositionId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,documents:page.Items.Any(x=>x.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapGet("/positions/{positionId}",async(string positionId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(positionId);var (session,customer,before)=await Resolve(http,owners,access).ConfigureAwait(false);
            var value=await client.Position(session,customer.CustomerId,positionId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Bound(value,customer.CustomerId,DateTimeOffset.UtcNow)||value.PositionId!=positionId)throw Invalid();
            await Confirm(http,owners,access,session,customer,before,documents:value.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapPost("/calculations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateNetWorth>(http).ConfigureAwait(false);
            if(!NetWorthData.Operation(input.OperationId))throw new TenantBoundaryException("NET_WORTH_INVALID",400);Identifier(input.PositionId);
            var position=await client.Position(session,customer.CustomerId,input.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Bound(position,customer.CustomerId,DateTimeOffset.UtcNow)||position.PositionId!=input.PositionId)throw Invalid();
            var receipt=await client.Calculate(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||!NetWorthData.Same(receipt.Position,position))throw Invalid();
            var saved=await client.Calculation(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Same(receipt,saved))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/calculations/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.Calculation(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);
            if(!Bound(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var position=await client.Position(session,customer.CustomerId,value.Position.PositionId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Same(position,value.Position))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/calculations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.Calculations(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>Bound(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/liquidity-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateLiquidityRatio>(http).ConfigureAwait(false);if(!LiquidityRatioData.Request(input))throw new TenantBoundaryException("LIQUIDITY_RATIO_INVALID",400);
            var net=await client.Calculation(session,customer.CustomerId,input.NetWorthCalculationId,http.RequestAborted).ConfigureAwait(false);
            var monthly=await client.MonthlyCashflowCalculation(session,customer.CustomerId,input.MonthlyCashflowCalculationId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Calculation(net,customer.CustomerId)||net.CalculationId!=input.NetWorthCalculationId||!MonthlyCashflowData.Calculation(monthly,customer.CustomerId)||monthly.CalculationId!=input.MonthlyCashflowCalculationId)throw Invalid();
            var receipt=await client.CalculateLiquidityRatio(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!LiquidityRatioData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||
               !NetWorthData.Same(receipt.SourceNetWorth,net)||!MonthlyCashflowData.Same(receipt.SourceMonthlyCashflow,monthly)||
               !receipt.LiquidAssetLineIds.SequenceEqual(input.LiquidAssetLineIds)||receipt.LiquidAssetsComplete!=input.LiquidAssetsComplete)throw Invalid();
            var saved=await client.LiquidityRatio(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!NetWorthData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:net.Position.Lines.Any(x=>x.Source is not null)||monthly.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/liquidity-ratios/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.LiquidityRatio(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!LiquidityRatioData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var net=await client.Calculation(session,customer.CustomerId,value.SourceNetWorth.CalculationId,http.RequestAborted).ConfigureAwait(false);
            var monthly=await client.MonthlyCashflowCalculation(session,customer.CustomerId,value.SourceMonthlyCashflow.CalculationId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Same(net,value.SourceNetWorth)||!MonthlyCashflowData.Same(monthly,value.SourceMonthlyCashflow))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:net.Position.Lines.Any(x=>x.Source is not null)||monthly.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/liquidity-ratios",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.LiquidityRatios(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>LiquidityRatioData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.SourceNetWorth.Position.Lines.Any(y=>y.Source is not null)||x.SourceMonthlyCashflow.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
        group.MapPost("/asset-allocations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var input=await CustomerReviewHttp.ReadAsync<CalculateAssetAllocation>(http).ConfigureAwait(false);if(!AssetAllocationData.Request(input))throw new TenantBoundaryException("ASSET_ALLOCATION_INVALID",400);
            var source=await client.Calculation(session,customer.CustomerId,input.NetWorthCalculationId,http.RequestAborted).ConfigureAwait(false);
            if(!NetWorthData.Calculation(source,customer.CustomerId)||source.CalculationId!=input.NetWorthCalculationId)throw Invalid();
            var receipt=await client.CalculateAssetAllocation(session,customer.CustomerId,input,http.RequestAborted).ConfigureAwait(false);
            if(!AssetAllocationData.Calculation(receipt,customer.CustomerId)||receipt.OperationId!=input.OperationId||receipt.RecordedBy!=session.ActorId||!NetWorthData.Same(receipt.SourceNetWorth,source))throw Invalid();
            var saved=await client.AssetAllocation(session,customer.CustomerId,receipt.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!NetWorthData.Same(saved,receipt))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return saved;
        });
        group.MapGet("/asset-allocations/{calculationId}",async(string calculationId,HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            CustomerReviewHttp.NoQuery(http.Request);Identifier(calculationId);var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);
            var value=await client.AssetAllocation(session,customer.CustomerId,calculationId,http.RequestAborted).ConfigureAwait(false);if(!AssetAllocationData.Calculation(value,customer.CustomerId)||value.CalculationId!=calculationId)throw Invalid();
            var source=await client.Calculation(session,customer.CustomerId,value.SourceNetWorth.CalculationId,http.RequestAborted).ConfigureAwait(false);if(!NetWorthData.Same(source,value.SourceNetWorth))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:source.Position.Lines.Any(x=>x.Source is not null)).ConfigureAwait(false);return value;
        });
        group.MapGet("/asset-allocations",async(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,ProductNetWorthClient client)=>
        {
            var (session,customer,before)=await Resolve(http,owners,access,calculate:true).ConfigureAwait(false);var cursor=CustomerReviewHttp.Cursor(http.Request);
            var page=await client.AssetAllocations(session,customer.CustomerId,cursor,http.RequestAborted).ConfigureAwait(false);
            if(!Page(page.Items,page.HasMore,page.NextBeforeId,x=>AssetAllocationData.Calculation(x,customer.CustomerId),x=>x.CalculationId)||page.Items.Any(x=>x.CalculationId==cursor))throw Invalid();
            await Confirm(http,owners,access,session,customer,before,calculate:true,documents:page.Items.Any(x=>x.SourceNetWorth.Position.Lines.Any(y=>y.Source is not null))).ConfigureAwait(false);return page;
        });
    }
    public static bool Bound(NetWorthCalculation? value,string customer)=>NetWorthData.Calculation(value,customer);
    private static bool Page<T>(IReadOnlyList<T>? items,bool more,string? next,Func<T,bool> bound,Func<T,string> id)=>items is not null&&items.Count<=50&&items.All(x=>x is not null&&bound(x))&&items.Select(id).Distinct(StringComparer.Ordinal).Count()==items.Count&&(more?items.Count==50&&next==id(items[^1]):next is null);
    private static void Identifier(string id){if(!TenantAccessClient.Identifier(id,80))throw new TenantBoundaryException("NET_WORTH_INVALID",400);}
    private static TenantBoundaryException Invalid()=>new("NET_WORTH_RESPONSE_INVALID",503);
    private static async Task<(CustomerOwnerSession Session,CustomerOwnerWorkspace Customer,CustomerAccessDecision Decision)> Resolve(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,bool write=false,bool calculate=false,bool documents=false)
    {
        var session=await owners.SessionAsync(http).ConfigureAwait(false);var customer=await owners.OwnedCustomerAsync(session.TenantId,session.ActorId,http.RequestAborted).ConfigureAwait(false)??throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED",403);
        var decision=await CustomerReviewHttp.AuthorizeAsync(http,customer.CustomerId,access,write:write,calculate:calculate,documents:documents).ConfigureAwait(false);
        if(decision.ActorId!=session.ActorId||decision.SubjectVersion!=session.SubjectVersion)throw new TenantBoundaryException("ACCESS_DENIED",403);return(session,customer,decision);
    }
    private static async Task Confirm(HttpContext http,CustomerOwnerClient owners,TenantAccessClient access,CustomerOwnerSession session,CustomerOwnerWorkspace customer,CustomerAccessDecision before,bool write=false,bool calculate=false,bool documents=false)
    {
        var (current,owned,after)=await Resolve(http,owners,access,write,calculate,documents).ConfigureAwait(false);CustomerReviewHttp.Confirm(before,after);
        if(current.TenantId!=session.TenantId||current.ActorId!=session.ActorId||current.SubjectVersion!=session.SubjectVersion||owned.CustomerId!=customer.CustomerId||owned.Version!=customer.Version)throw new TenantBoundaryException("NET_WORTH_CONTEXT_CHANGED",409);
    }
}
public sealed class ProductNetWorthClient:IDisposable
{
    private readonly HttpClient client;private readonly Uri profile,rules;
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public ProductNetWorthClient(IConfiguration config):this(config,CreateClient()){}
    public ProductNetWorthClient(IConfiguration config,HttpClient client)
    {this.client=client;profile=TenantAccessIntegration.LoopbackRoot(config["Monergy:Product:FinancialProfileUrl"]??throw new InvalidOperationException("Financial Profile endpoint required."));rules=TenantAccessIntegration.LoopbackRoot(config["Monergy:Product:FinancialRulesUrl"]??throw new InvalidOperationException("Financial Rules endpoint required."));}
    private static HttpClient CreateClient()=>new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(30),MaxResponseContentBufferSize=33554432};
    public Task<ReviewedNetWorthPosition> SavePosition(CustomerOwnerSession s,string c,CreateNetWorthPosition input,CancellationToken ct)=>Send<ReviewedNetWorthPosition>(profile,s,c,"positions",input,ct);
    public Task<ReviewedNetWorthPosition> Position(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<ReviewedNetWorthPosition>(profile,s,c,"positions/"+Uri.EscapeDataString(id),null,ct);
    public Task<NetWorthPositionPage> Positions(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<NetWorthPositionPage>(profile,s,c,"positions"+Cursor(cursor),null,ct);
    public Task<NetWorthCalculation> Calculate(CustomerOwnerSession s,string c,CalculateNetWorth input,CancellationToken ct)=>Send<NetWorthCalculation>(rules,s,c,"calculations",input,ct);
    public Task<NetWorthCalculation> Calculation(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<NetWorthCalculation>(rules,s,c,"calculations/"+Uri.EscapeDataString(id),null,ct);
    public Task<NetWorthCalculationPage> Calculations(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<NetWorthCalculationPage>(rules,s,c,"calculations"+Cursor(cursor),null,ct);
    public Task<MonthlyCashflowCalculation> MonthlyCashflowCalculation(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>SendAt<MonthlyCashflowCalculation>(rules,s,c,"monthly-cashflow","calculations/"+Uri.EscapeDataString(id),null,ct);
    public Task<LiquidityRatioCalculation> CalculateLiquidityRatio(CustomerOwnerSession s,string c,CalculateLiquidityRatio input,CancellationToken ct)=>Send<LiquidityRatioCalculation>(rules,s,c,"liquidity-ratios",input,ct);
    public Task<LiquidityRatioCalculation> LiquidityRatio(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<LiquidityRatioCalculation>(rules,s,c,"liquidity-ratios/"+Uri.EscapeDataString(id),null,ct);
    public Task<LiquidityRatioCalculationPage> LiquidityRatios(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<LiquidityRatioCalculationPage>(rules,s,c,"liquidity-ratios"+Cursor(cursor),null,ct);
    public Task<AssetAllocationCalculation> CalculateAssetAllocation(CustomerOwnerSession s,string c,CalculateAssetAllocation input,CancellationToken ct)=>Send<AssetAllocationCalculation>(rules,s,c,"asset-allocations",input,ct);
    public Task<AssetAllocationCalculation> AssetAllocation(CustomerOwnerSession s,string c,string id,CancellationToken ct)=>Send<AssetAllocationCalculation>(rules,s,c,"asset-allocations/"+Uri.EscapeDataString(id),null,ct);
    public Task<AssetAllocationCalculationPage> AssetAllocations(CustomerOwnerSession s,string c,string? cursor,CancellationToken ct)=>Send<AssetAllocationCalculationPage>(rules,s,c,"asset-allocations"+Cursor(cursor),null,ct);
    private static string Cursor(string? value)=>value is null?"":"?beforeId="+Uri.EscapeDataString(value);
    private Task<T> Send<T>(Uri root,CustomerOwnerSession session,string customer,string suffix,object? input,CancellationToken ct)=>SendAt<T>(root,session,customer,"net-worth",suffix,input,ct);
    private async Task<T> SendAt<T>(Uri root,CustomerOwnerSession session,string customer,string area,string suffix,object? input,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(input is null?HttpMethod.Get:HttpMethod.Post,new Uri(root,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/"+area+"/"+suffix));
        request.Headers.Add("X-Monergy-Tenant",session.TenantId);request.Headers.Add("X-Monergy-Session",session.AuthenticationContextId);if(input is not null)request.Content=JsonContent.Create(input,options:ContractJson.Options);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new TenantBoundaryException("NET_WORTH_UNAVAILABLE",(int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 413?(int)response.StatusCode:503);
        return await response.Content.ReadFromJsonAsync<T>(Json,ct).ConfigureAwait(false)??throw new TenantBoundaryException("NET_WORTH_RESPONSE_INVALID",503);
    }
    public void Dispose()=>client.Dispose();
}
