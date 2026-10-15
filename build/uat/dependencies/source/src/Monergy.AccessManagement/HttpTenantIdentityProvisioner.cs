using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

// Development/CI owner port. The selected owner path never falls back to a fixture.
public sealed class HttpTenantIdentityProvisioner : ITenantIdentityProvisioner, IDisposable
{
    private readonly HttpClient client;
    private readonly ReferenceDeliveryRoute route;

    public HttpTenantIdentityProvisioner(HttpClient client, ReferenceDeliveryRoute route)
    {
        ArgumentNullException.ThrowIfNull(client);
        ReferenceOwnerBoundary.Validate(route);
        this.client = client;
        this.route = route;
        client.Timeout = TimeSpan.FromSeconds(5);
        client.MaxResponseContentBufferSize = 16_384;
    }

    public async ValueTask<IdentityProvisioningReceipt> ProvisionAsync(IdentityProvisioningRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(route.Endpoint, "internal/v1/tenant-identities/provision"));
            message.Headers.Add("X-Monergy-Owner-Token", route.Token);
            message.Headers.Add("X-Monergy-Tenant", request.TenantId);
            message.Content = JsonContent.Create(request, options: AccessEventProtocol.Json);
            using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new AccessAdministrationException("IDENTITY_PROVISIONING_CONFLICT", 409);
            if (!response.IsSuccessStatusCode) throw new IdentityProvisioningUnavailableException();
            var receipt = await response.Content.ReadFromJsonAsync<IdentityProvisioningReceipt>(AccessEventProtocol.Json, ct).ConfigureAwait(false);
            if (receipt is null || receipt.TenantId != request.TenantId || receipt.NormalizedEmail != request.NormalizedEmail ||
                receipt.IdempotencyKey != request.IdempotencyKey)
                throw new IdentityProvisioningUnavailableException();
            AccessInput.Identifier(receipt.ActorId);
            return receipt;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            throw new IdentityProvisioningUnavailableException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IdentityProvisioningUnavailableException();
        }
    }

    public void Dispose() => client.Dispose();
}
