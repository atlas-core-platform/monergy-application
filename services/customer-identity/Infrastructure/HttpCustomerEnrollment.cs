using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;
namespace Monergy.Services.CustomerIdentity.Infrastructure;
public sealed class HttpCustomerEnrollment:ICustomerEnrollmentClient,IDisposable
{
    private readonly HttpClient client=new(new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(5),MaxResponseContentBufferSize=4096};
    private readonly string token;
    public HttpCustomerEnrollment(IConfiguration configuration)
    {
        client.BaseAddress=TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration,"Monergy:AccessIntegration:AccessManagementUrl"));
        token=TenantAccessIntegration.Required(configuration,"Monergy:CustomerRegistration:EnrollmentToken");TenantAccessIntegration.ValidateToken(token);
    }
    public async Task EnrollAsync(string tenant,string actor,string email,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"internal/v1/customer-enrollments"){Content=JsonContent.Create(new{actorId=actor,email})};
        request.Headers.Add("X-Monergy-Owner-Token",token);request.Headers.Add("X-Monergy-Tenant",tenant);
        try
        {
            using var response=await client.SendAsync(request,ct);
            if(!response.IsSuccessStatusCode)throw new TenantAccessException("ENROLLMENT_UNAVAILABLE",503);
            var receipt=await response.Content.ReadFromJsonAsync<Receipt>(cancellationToken:ct);
            if(receipt is null||receipt.TenantId!=tenant||receipt.ActorId!=actor||receipt.Email!=email)throw new TenantAccessException("ENROLLMENT_UNAVAILABLE",503);
        }
        catch(Exception e)when(e is HttpRequestException or JsonException or OperationCanceledException){throw new TenantAccessException("ENROLLMENT_UNAVAILABLE",503);}
    }
    public void Dispose()=>client.Dispose();
    private sealed record Receipt(string TenantId,string ActorId,string Email);
}
