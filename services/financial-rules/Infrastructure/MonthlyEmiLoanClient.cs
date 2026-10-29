using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.FinancialRules.Application;
namespace Monergy.Services.FinancialRules.Infrastructure;

public sealed class MonthlyEmiLoanClient:IMonthlyEmiLoanReader,IDisposable
{
    private readonly HttpClient client;private readonly Uri root;
    private static readonly JsonSerializerOptions Json=new(ContractJson.Options){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public MonthlyEmiLoanClient(IConfiguration config):this(config,new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false})){}
    public MonthlyEmiLoanClient(IConfiguration config,HttpClient client)
    {this.client=client;client.Timeout=TimeSpan.FromSeconds(15);client.MaxResponseContentBufferSize=1048576;root=TenantAccessIntegration.LoopbackRoot(config["Monergy:MonthlyCashflow:FinancialProfileUrl"]??throw new InvalidOperationException("Monthly EMI share requires its Financial Profile owner endpoint."));}
    public async Task<ReviewedLoanRecord> ReadAsync(string tenant,string session,string customer,string id,int revision,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(root,"local/v1/customers/"+Uri.EscapeDataString(customer)+"/loan-records/"+Uri.EscapeDataString(id)+"?revision="+revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        request.Headers.Add("X-Monergy-Tenant",tenant);request.Headers.Add("X-Monergy-Session",session);
        using var response=await client.SendAsync(request,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new TenantBoundaryException("MONTHLY_EMI_LOAN_UNAVAILABLE",(int)response.StatusCode is 400 or 401 or 403 or 404 or 409?(int)response.StatusCode:503);
        return await response.Content.ReadFromJsonAsync<ReviewedLoanRecord>(Json,ct).ConfigureAwait(false)??throw new TenantBoundaryException("MONTHLY_EMI_LOAN_INVALID",503);
    }
    public void Dispose()=>client.Dispose();
}
