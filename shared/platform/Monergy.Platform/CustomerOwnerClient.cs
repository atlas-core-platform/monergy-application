using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public sealed record CustomerOwnerContext(string TenantId, string CustomerId, string? OwnerActorId, long Version, bool Active);
public sealed record CustomerOwnerSession(string TenantId, string ActorId, string AuthenticationContextId, long SubjectVersion, DateTimeOffset ExpiresAt);
public sealed record CustomerOwnerMembership(string TenantId, string ActorId, bool Active, long SubjectVersion, long PolicyVersion);

// LOCAL/CI owner ports only. Workload tokens never come from request headers or
// leave their intended destination. Session validation retains C&I authority.
public sealed class CustomerOwnerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client;
    private readonly Uri identity;
    private readonly Uri access;
    private readonly string contextToken;
    private readonly string sessionToken;
    private readonly string membershipToken;

    public CustomerOwnerClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }))
    { }

    public CustomerOwnerClient(IConfiguration configuration, HttpClient httpClient)
    {
        TenantAccessIntegration.EnsureAllowed(configuration);
        client = httpClient;
        client.Timeout = TimeSpan.FromSeconds(5); client.MaxResponseContentBufferSize = 16_384;
        identity = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:CustomerIdentityUrl"));
        access = TenantAccessIntegration.LoopbackRoot(TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:AccessManagementUrl"));
        contextToken = TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:CustomerContextToken");
        sessionToken = TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:CustomerIdentityToken");
        membershipToken = TenantAccessIntegration.Required(configuration, "Monergy:AccessIntegration:MembershipToken");
        foreach (var token in new[] { contextToken, sessionToken, membershipToken }) TenantAccessIntegration.ValidateToken(token);
        if (new[] { contextToken, sessionToken, membershipToken }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidOperationException("Owner ports require separate workload credentials.");
    }

    public async Task<CustomerOwnerContext> CustomerAsync(string tenant, string customer, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(customer, 128)) throw new TenantBoundaryException("INVALID_CUSTOMER", 400);
        var value = await SendAsync<CustomerOwnerContext>(identity, "internal/v1/customer-context/" + Uri.EscapeDataString(customer),
            tenant, contextToken, null, ct).ConfigureAwait(false);
        if (value.TenantId != tenant || value.CustomerId != customer || value.Version < 1 || !value.Active)
            throw new TenantBoundaryException("CUSTOMER_UNAVAILABLE", 503);
        return value;
    }

    public async Task<CustomerOwnerSession> SessionAsync(HttpContext http)
    {
        var (tenant, session) = TenantAccessClient.Headers(http);
        var value = await SendAsync<CustomerOwnerSession>(identity, "internal/v1/tenant-sessions/validate", tenant, sessionToken,
            new { tenantId = tenant, authenticationContextId = session }, http.RequestAborted).ConfigureAwait(false);
        if (value.TenantId != tenant || value.AuthenticationContextId != session || value.SubjectVersion < 1 ||
            !TenantAccessClient.Identifier(value.ActorId, 128) || value.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new TenantBoundaryException("TENANT_SESSION_INVALID", 401);
        return value;
    }

    public async Task RequireActiveMemberAsync(string tenant, string actor, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(actor, 128)) throw new TenantBoundaryException("INVALID_ACTOR", 400);
        var value = await SendAsync<CustomerOwnerMembership>(access, "internal/v1/access-subjects/" + Uri.EscapeDataString(actor),
            tenant, membershipToken, null, ct).ConfigureAwait(false);
        if (value.TenantId != tenant || value.ActorId != actor || !value.Active || value.SubjectVersion < 1 || value.PolicyVersion < 1)
            throw new TenantBoundaryException("MEMBERSHIP_REQUIRED", 409);
    }

    private async Task<T> SendAsync<T>(Uri endpoint, string path, string tenant, string token, object? body, CancellationToken ct)
    {
        if (!TenantAccessClient.Identifier(tenant, 64)) throw new TenantBoundaryException("INVALID_TENANT", 400);
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(endpoint, path));
            request.Headers.Add("X-Monergy-Tenant", tenant); request.Headers.Add("X-Monergy-Owner-Token", token);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TenantBoundaryException("CUSTOMER_OWNER_UNAVAILABLE",
                response.StatusCode == HttpStatusCode.Unauthorized ? 401 : response.StatusCode == HttpStatusCode.NotFound ? 404 : 503);
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("CUSTOMER_OWNER_RESPONSE_INVALID", 503);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new TenantBoundaryException("CUSTOMER_OWNER_UNAVAILABLE", 503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TenantBoundaryException("CUSTOMER_OWNER_UNAVAILABLE", 503); }
    }

    public void Dispose() => client.Dispose();
}
