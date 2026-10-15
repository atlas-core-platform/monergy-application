using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

// Only the separately guarded Development tool constructs this adapter. No URL
// or transport credential is accepted from a tenant administration request.
public sealed class ReferenceHttpEventTransport : IAccessEventTransport
{
    private readonly HttpClient client;
    private readonly Dictionary<string, ReferenceDeliveryRoute> routes;

    public ReferenceHttpEventTransport(HttpClient client, Uri endpoint, string token)
        : this(client, new Dictionary<string, ReferenceDeliveryRoute>(StringComparer.Ordinal)
        {
            ["authorization"] = new(endpoint, token),
            ["sessions"] = new(endpoint, token),
            ["audit"] = new(endpoint, token)
        }) { }

    public ReferenceHttpEventTransport(HttpClient client, IReadOnlyDictionary<string, ReferenceDeliveryRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count != 3 || !routes.ContainsKey("authorization") || !routes.ContainsKey("sessions") || !routes.ContainsKey("audit"))
            throw new ArgumentException("All owner delivery routes are required.");
        foreach (var route in routes.Values) ReferenceOwnerBoundary.Validate(route);
        this.client = client;
        this.routes = new Dictionary<string, ReferenceDeliveryRoute>(routes, StringComparer.Ordinal);
        client.Timeout = TimeSpan.FromSeconds(5);
        client.MaxResponseContentBufferSize = 65_536;
    }

    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    public async Task<AccessEventReceipt> PublishAsync(string destination, AccessEventEnvelope message, CancellationToken ct)
    {
        AccessEventProtocol.Validate(message, message.TenantId, destination);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes[destination].Endpoint, "reference/events/" + destination));
        request.Headers.Add("X-Monergy-Reference-Transport", routes[destination].Token);
        request.Headers.Add("X-Monergy-Reference-Tenant", message.TenantId);
        request.Content = JsonContent.Create(message, options: AccessEventProtocol.Json);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new EventDeliveryException("HTTP_DELIVERY_REJECTED",
                    (int)response.StatusCode < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests));
            var receipt = await response.Content.ReadFromJsonAsync<AccessEventReceipt>(AccessEventProtocol.Json, ct).ConfigureAwait(false);
            AccessEventProtocol.ValidateReceipt(receipt, message, destination);
            return receipt!;
        }
        catch (JsonException) { throw new EventDeliveryException("INVALID_RECEIPT", true); }
        catch (HttpRequestException) { throw new EventDeliveryException("TRANSPORT_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EventDeliveryException("TRANSPORT_TIMEOUT"); }
    }
}
