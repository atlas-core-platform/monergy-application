using System.Net.Http.Json;
using System.Text.Json;

namespace Monergy.AccessManagement;

public interface IPlatformIdentityClient
{
    Task<VerifiedPlatformContext?> ResolveAsync(PlatformReviewEvaluationRequest request, CancellationToken ct);
    Task<VerifiedPlatformPrincipal?> FindPrincipalAsync(string principalId, CancellationToken ct);
}

public sealed class PlatformIdentityClient : IPlatformIdentityClient, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 16_384 };
    private readonly ReferenceDeliveryRoute route;
    private readonly TimeProvider time;

    public PlatformIdentityClient(ReferenceDeliveryRoute route, TimeProvider time)
    {
        ReferenceOwnerBoundary.Validate(route);
        this.route = route;
        this.time = time;
    }

    public async Task<VerifiedPlatformContext?> ResolveAsync(PlatformReviewEvaluationRequest request, CancellationToken ct)
    {
        var identity = await SendAsync<VerifiedPlatformContext>("internal/platform-identity/v1/validate", request, ct).ConfigureAwait(false);
        if (identity is null) return null;
        var now = time.GetUtcNow();
        return PlatformReviewerContract.IsIdentifier(identity.PrincipalId) && identity.Realm == PlatformReviewerContract.Realm &&
            identity.CredentialVersion > 0 && identity.ExpiresAt > now && identity.ExpiresAt <= now.AddMinutes(5) &&
            identity.AuthenticationContext == request.AuthenticationContext && identity.Operation == request.Operation &&
            identity.ResourceId == request.ResourceId ? identity : null;
    }

    public async Task<VerifiedPlatformPrincipal?> FindPrincipalAsync(string principalId, CancellationToken ct)
    {
        var identity = await SendAsync<VerifiedPlatformPrincipal>("internal/platform-identity/v1/principals/validate",
            new { principalId }, ct).ConfigureAwait(false);
        return identity is { CredentialVersion: > 0 } && identity.PrincipalId == principalId &&
            identity.Realm == PlatformReviewerContract.Realm ? identity : null;
    }

    private async Task<T?> SendAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(route.Endpoint, path));
        request.Headers.Add("X-Monergy-Platform-Identity", route.Token);
        request.Content = JsonContent.Create(body, options: PlatformReviewerContract.Json);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                return default;
            if (!response.IsSuccessStatusCode) throw new PlatformAuthorizationException("PLATFORM_IDENTITY_UNAVAILABLE", 503);
            return await response.Content.ReadFromJsonAsync<T>(PlatformReviewerContract.Json, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new PlatformAuthorizationException("PLATFORM_IDENTITY_UNAVAILABLE", 503);
        }
    }

    public void Dispose() => client.Dispose();
}
