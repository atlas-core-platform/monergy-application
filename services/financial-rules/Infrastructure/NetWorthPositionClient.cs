using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
namespace Monergy.Services.FinancialRules.Infrastructure;
public sealed class NetWorthPositionClient:INetWorthPositionReader,IDisposable
{
    private readonly HttpClient client;private readonly Uri root;
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public NetWorthPositionClient(IConfiguration config):this(config,new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false})){}
    public NetWorthPositionClient(IConfiguration config,HttpClient client)
    {this.client=client;client.Timeout=TimeSpan.FromSeconds(15);client.MaxResponseContentBufferSize=1048576;root=TenantAccessIntegration.LoopbackRoot(config["Monergy:NetWorth:FinancialProfileUrl"]??throw new InvalidOperationException("Net worth requires its Financial Profile owner endpoint."));}
    public async Task<ReviewedNetWorthPosition> ReadAsync(string tenant,string session,string customer,string id,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(root,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/net-worth/positions/"+Uri.EscapeDataString(id)));
        request.Headers.Add("X-Monergy-Tenant",tenant);request.Headers.Add("X-Monergy-Session",session);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new TenantBoundaryException("NET_WORTH_POSITION_UNAVAILABLE",(int)response.StatusCode is 400 or 401 or 403 or 404 or 409?(int)response.StatusCode:503);
        return await response.Content.ReadFromJsonAsync<ReviewedNetWorthPosition>(Json,ct).ConfigureAwait(false)??throw new TenantBoundaryException("NET_WORTH_POSITION_INVALID",503);
    }
    public void Dispose()=>client.Dispose();
}
