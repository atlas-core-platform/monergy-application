using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.CustomerIdentity.Application;

namespace Monergy.Services.CustomerIdentity.Infrastructure;

public sealed class HttpTenantMembershipClient : ITenantMembershipClient, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string token;

    public HttpTenantMembershipClient(IConfiguration configuration)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        endpoint = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:AccessManagementUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:MembershipToken");
        TenantAccessIntegration.ValidateToken(token);
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 65_536,
        };
    }

    public async Task<TenantMembershipState> ReadAsync(string tenantId, string actorId, CancellationToken cancellationToken)
    {
        TenantAccessProtocol.Identifier(tenantId, 64);
        TenantAccessProtocol.Identifier(actorId);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "internal/v1/access-subjects/" + Uri.EscapeDataString(actorId)));
        request.Headers.Add("X-Monergy-Owner-Token", token);
        request.Headers.Add("X-Monergy-Tenant", tenantId);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new TenantAccessException("MEMBERSHIP_REQUIRED", 401);
            if (!response.IsSuccessStatusCode) throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
            var state = await response.Content.ReadFromJsonAsync<TenantMembershipState>(TenantAccessProtocol.Json, cancellationToken).ConfigureAwait(false);
            if (state is null || state.TenantId != tenantId || state.ActorId != actorId || state.SubjectVersion < 1 || state.PolicyVersion < 1)
                throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
            return state;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TenantAccessException("MEMBERSHIP_UNAVAILABLE", 503);
        }
    }

    public void Dispose() => client.Dispose();
}
