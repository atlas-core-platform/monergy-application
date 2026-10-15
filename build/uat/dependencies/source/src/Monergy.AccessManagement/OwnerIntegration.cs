using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Monergy.AccessManagement;

public sealed record TenantMembershipState(string TenantId, string ActorId, bool Active, long SubjectVersion, long PolicyVersion);
public sealed record TenantSessionLookup(string TenantId, string AuthenticationContextId);
public sealed record TenantSessionContext(string TenantId, string ActorId, string AuthenticationContextId,
    long SubjectVersion, DateTimeOffset ExpiresAt);
public sealed record ReferenceDeliveryRoute(Uri Endpoint, string Token);

public static class ReferenceOwnerBoundary
{
    public static void Validate(ReferenceDeliveryRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var endpoint = route.Endpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/")
            throw new ArgumentException("Owner reference integration requires a loopback root URL.");
        ValidateToken(route.Token);
    }

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 || token.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Owner reference integration requires a bounded local workload token.");
    }

    public static bool TokenMatches(string actual, string expected) =>
        actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    public static void MapMembershipPort(this WebApplication app, string token)
    {
        ValidateToken(token);
        app.MapGet("/internal/v1/access-subjects/{actorId}", async (string actorId, HttpContext http, IAccessPolicyStore store) =>
        {
            if (!TokenMatches(http.Request.Headers["X-Monergy-Owner-Token"].ToString(), token))
                return (IResult)Results.StatusCode(403);
            var tenant = http.Request.Headers["X-Monergy-Tenant"].ToString();
            try
            {
                AccessInput.Identifier(tenant, 64);
                AccessInput.Identifier(actorId);
                var state = await store.GetSubjectPolicyStateAsync(tenant, actorId, http.RequestAborted).ConfigureAwait(false);
                return state is null ? Results.NotFound() : Results.Ok(new TenantMembershipState(tenant, actorId,
                    state.Subject.Status == MembershipStatus.Active, state.Subject.SubjectVersion, state.PolicyVersion));
            }
            catch (AccessAdministrationException) { return Results.BadRequest(); }
            catch (AccessStoreUnavailableException) { return Results.StatusCode(503); }
        });
    }
}

// Explicit Development-only owner integration. Caller actor/workload headers
// are ignored; Customer & Identity resolves the canonical tenant/session pair.
public sealed class CustomerIdentityContextResolver : ITrustedSecurityContextResolver, IDisposable
{
    private readonly HttpClient client;
    private readonly ReferenceDeliveryRoute route;
    private readonly TimeProvider clock;

    public CustomerIdentityContextResolver(HttpClient client, ReferenceDeliveryRoute route, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ReferenceOwnerBoundary.Validate(route);
        this.client = client;
        this.route = route;
        this.clock = clock;
        client.Timeout = TimeSpan.FromSeconds(5);
        client.MaxResponseContentBufferSize = 65_536;
    }

    public bool IsReady => true;

    public async ValueTask<(TrustedActorContext? Context, string? Failure, bool Available)> ResolveAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var tenant = httpContext.Request.Headers["X-Monergy-Tenant"].ToString();
        var session = httpContext.Request.Headers["X-Monergy-Session"].ToString();
        try { AccessInput.Identifier(tenant, 64); AccessInput.Identifier(session, 160); }
        catch (AccessAdministrationException) { return (null, "TENANT_SESSION_REQUIRED", true); }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route.Endpoint, "internal/v1/tenant-sessions/validate"));
            request.Headers.Add("X-Monergy-Owner-Token", route.Token);
            request.Content = JsonContent.Create(new TenantSessionLookup(tenant, session), options: AccessEventProtocol.Json);
            using var response = await client.SendAsync(request, httpContext.RequestAborted).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                return (null, "TENANT_SESSION_INVALID", true);
            if (!response.IsSuccessStatusCode) return (null, "TRUSTED_CONTEXT_UNAVAILABLE", false);
            var context = await response.Content.ReadFromJsonAsync<TenantSessionContext>(AccessEventProtocol.Json, httpContext.RequestAborted).ConfigureAwait(false);
            if (context is null || context.TenantId != tenant || context.AuthenticationContextId != session ||
                context.SubjectVersion < 1 || context.ExpiresAt <= clock.GetUtcNow())
                return (null, "TRUSTED_CONTEXT_UNAVAILABLE", false);
            AccessInput.Identifier(context.ActorId);
            return (new(tenant, context.ActorId, session, "access-management-service", httpContext.TraceIdentifier), null, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or AccessAdministrationException)
        {
            return (null, "TRUSTED_CONTEXT_UNAVAILABLE", false);
        }
        catch (OperationCanceledException) when (!httpContext.RequestAborted.IsCancellationRequested)
        {
            return (null, "TRUSTED_CONTEXT_UNAVAILABLE", false);
        }
    }

    public void Dispose() => client.Dispose();
}
