using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Platform;

namespace Monergy.Services.AiIntelligence.AssistantFeedback;

public sealed class FeedbackAuthority(IConfiguration configuration) : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 16384 };

    public async Task<PlatformReviewDecision> RequireAsync(string authenticationContext, string operation, string? resource, CancellationToken ct)
    {
        var configured = configuration["Monergy:AssistantFeedback:AccessManagementUrl"] ?? "http://127.0.0.1:5088/";
        if (configured.TrimEnd('/') != "http://127.0.0.1:5088") throw new InvalidOperationException("AM platform destination invalid.");
        var token = configuration["Monergy:AssistantFeedback:PlatformEvaluationToken"];
        if (token is not { Length: >= 32 }) throw new InvalidOperationException("AM platform authorization unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(configured.TrimEnd('/') + "/platform/v1/assistant-feedback/evaluate"));
        request.Headers.Add("X-Monergy-AM-Platform", token);
        request.Content = JsonContent.Create(new PlatformContextValidation(authenticationContext, operation, resource), options: AssistantContract.Json);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new TenantBoundaryException("FEEDBACK_REVIEW_AUTHORIZATION_UNAVAILABLE", 403);
        var decision = await response.Content.ReadFromJsonAsync<PlatformReviewDecision>(AssistantContract.Json, ct).ConfigureAwait(false)
            ?? throw new JsonException();
        if (!decision.Allowed) throw new TenantBoundaryException("FEEDBACK_REVIEW_REQUIRED", 403);
        var now = DateTimeOffset.UtcNow;
        if (!AssistantContract.OpaqueId(decision.DecisionId) || decision.PrincipalId is null || !AssistantContract.Code(decision.PrincipalId) ||
            decision.Realm != AssistantContract.Realm || decision.CredentialVersion < 1 || decision.Capability != AssistantContract.Capability ||
            decision.Operation != operation || decision.ResourceId != resource || decision.PolicyRevision < 1 || decision.GrantRevision < 0 ||
            decision.EvaluatedAt > now.AddSeconds(2) || decision.EvaluatedAt < now.AddSeconds(-15) ||
            decision.ExpiresAt <= now || decision.ExpiresAt > now.AddSeconds(30) || decision.AuditState is not ("Pending" or "Accepted"))
            throw new TenantBoundaryException("FEEDBACK_REVIEW_DECISION_INVALID", 403);
        return decision;
    }

    public async Task<PlatformReviewDecision> ReconfirmAsync(PlatformReviewDecision previous, string context, CancellationToken ct)
    {
        var current = await RequireAsync(context, previous.Operation, previous.ResourceId, ct).ConfigureAwait(false);
        if (current.PrincipalId != previous.PrincipalId || current.Realm != previous.Realm || current.CredentialVersion != previous.CredentialVersion ||
            current.PolicyRevision != previous.PolicyRevision || current.GrantRevision != previous.GrantRevision)
            throw new TenantBoundaryException("FEEDBACK_REVIEW_AUTHORITY_CHANGED", 403);
        return current;
    }
    public void Dispose() => client.Dispose();
}
