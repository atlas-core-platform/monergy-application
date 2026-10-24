using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Monergy.Platform;

public sealed record TenantAdminContext(string TenantId, string ActorId, long PolicyVersion, string Authority);
public sealed record AdvisorEntryContext(string TenantId, string ActorId, string Authority,
    long PolicyVersion, long SubjectVersion, long RecordedRelationshipCount);
public sealed record TenantAccessRequest(string CapabilityId, string? ResourceType, string? ResourceId);
public sealed record CustomerAccessRequest(string CapabilityId, string CustomerId, string Purpose);
public sealed record CustomerAccessDecision(string DecisionId, string Outcome, string TenantId, string ActorId,
    string CustomerId, string CapabilityId, string Purpose, string ReasonCode, string? AccessBasis,
    long PolicyVersion, long SubjectVersion, long CustomerVersion, long ConsentVersion, DateTimeOffset EvaluatedAt);
public sealed record TenantAccessDecision(string DecisionId, string Outcome, string TenantId, string ActorId,
    string CapabilityId, string? ResourceType, string? ResourceId, string ReasonCode, long PolicyVersion,
    long SubjectVersion, string CapabilityCatalogVersion, DateTimeOffset EvaluatedAt, DateTimeOffset? ExpiresAt,
    string CacheMode, string CorrelationId);
public sealed class TenantBoundaryException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

// No shared SQL, caller-selected URL, forwarding of workload credentials, redirect,
// proxy, or external ALLOW cache. Every request reaches the current policy owner.
public sealed class TenantAccessClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    private readonly HttpClient client;
    private readonly Uri endpoint;

    public TenantAccessClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }))
    { }

    public TenantAccessClient(IConfiguration configuration, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
            throw new InvalidOperationException("Tenant access client requires a selected LOCAL/CI transport.");
        endpoint = TenantAccessIntegration.LoopbackRoot(configuration["Monergy:TenantAccess:AccessManagementUrl"]
            ?? configuration["Monergy:AccessIntegration:AccessManagementUrl"] ?? "http://127.0.0.1:5088/");
        client = httpClient;
        client.Timeout = TimeSpan.FromSeconds(5);
        client.MaxResponseContentBufferSize = 65_536;
    }

    // An Advisor route is distinct from Tenant Administration and customer ownership.
    public async Task<AdvisorEntryContext> RequireAdvisorAsync(HttpContext http)
    {
        var (tenant, session) = Headers(http);
        using var request = Request(HttpMethod.Get, "v1/advisor/context", tenant, session);
        AdvisorEntryContext value;
        try { value = await SendAsync<AdvisorEntryContext>(request, http.RequestAborted).ConfigureAwait(false); }
        catch (TenantBoundaryException error) when (error.Status == 403)
        { throw new TenantBoundaryException("ADVISOR_ROLE_REQUIRED", 403); }
        if (value.TenantId != tenant || !Identifier(value.ActorId, 128) ||
            value.Authority != "FinancialAdvisor" || value.PolicyVersion < 1 ||
            value.SubjectVersion < 1 || value.RecordedRelationshipCount < 0)
            throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        return value;
    }

    public async Task<TenantAdminContext> RequireAdministratorAsync(HttpContext http)
    {
        var (tenant, session) = Headers(http);
        using var request = Request(HttpMethod.Get, "v1/administration/context", tenant, session);
        var context = await SendAsync<TenantAdminContext>(request, http.RequestAborted).ConfigureAwait(false);
        if (context.TenantId != tenant || context.Authority != "TenantAdmin" || context.PolicyVersion < 1 || !Identifier(context.ActorId, 128))
            throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        return context;
    }

    public async Task<TenantAccessDecision> EvaluateAsync(HttpContext http, TenantAccessRequest evaluation)
    {
        var (tenant, session) = Headers(http);
        using var request = Request(HttpMethod.Post, "v1/authorization/evaluations", tenant, session);
        request.Content = JsonContent.Create(evaluation);
        var result = await SendAsync<TenantAccessDecision>(request, http.RequestAborted).ConfigureAwait(false);
        if (result.TenantId != tenant || result.CapabilityId != evaluation.CapabilityId || result.ResourceType != evaluation.ResourceType ||
            result.ResourceId != evaluation.ResourceId || !Identifier(result.ActorId, 128) || result.Outcome is not ("Allow" or "Deny") ||
            result.EvaluatedAt < DateTimeOffset.UtcNow.AddSeconds(-30) || result.EvaluatedAt > DateTimeOffset.UtcNow.AddSeconds(5) ||
            result.Outcome == "Allow" && (result.PolicyVersion < 1 || result.SubjectVersion < 1 || result.ReasonCode != "Granted"))
            throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        return result;
    }

    public static (string Tenant, string Session) Headers(HttpContext http)
    {
        var tenant = http.Request.Headers["X-Monergy-Tenant"];
        var session = http.Request.Headers["X-Monergy-Session"];
        if (tenant.Count != 1 || session.Count != 1 || !Identifier(tenant[0], 64) || !Identifier(session[0], 160))
            throw new TenantBoundaryException("TENANT_SESSION_REQUIRED", 401);
        return (tenant[0]!, session[0]!);
    }

    public async Task<CustomerAccessDecision> EvaluateCustomerAsync(HttpContext http, string capability, string customer)
    {
        var (tenant, session) = Headers(http);
        return await EvaluateCustomerAsync(tenant, session, capability, customer, http.RequestAborted).ConfigureAwait(false);
    }

    public async Task<CustomerAccessDecision> EvaluateCustomerAsync(
        string tenant,
        string session,
        string capability,
        string customer,
        CancellationToken cancellationToken = default)
    {
        if (!Identifier(tenant, 64) || !Identifier(session, 160) || !Identifier(capability, 160) ||
            !Identifier(customer, 128))
            throw new TenantBoundaryException("TENANT_ACCESS_REQUEST_INVALID", 400);
        const string purpose = "customer-advice"; // Owner-selected; never taken from browser input.
        using var request = Request(HttpMethod.Post, "v1/authorization/customer-evaluations", tenant, session);
        request.Content = JsonContent.Create(new CustomerAccessRequest(capability, customer, purpose));
        var result = await SendAsync<CustomerAccessDecision>(request, cancellationToken).ConfigureAwait(false);
        if (result.TenantId != tenant || result.CustomerId != customer || result.CapabilityId != capability || result.Purpose != purpose ||
            !Identifier(result.ActorId, 128) || !Identifier(result.DecisionId, 128) || result.Outcome is not ("Allow" or "Deny") ||
            result.EvaluatedAt < DateTimeOffset.UtcNow.AddSeconds(-10) || result.EvaluatedAt > DateTimeOffset.UtcNow.AddSeconds(5) ||
            result.Outcome == "Allow" && (result.PolicyVersion < 1 || result.SubjectVersion < 1 || result.CustomerVersion < 1 ||
                result.ReasonCode != "Granted" || result.AccessBasis is not ("Self" or "Advisor") || result.AccessBasis == "Advisor" && result.ConsentVersion < 1))
            throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        return result;
    }

    public static bool Identifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    private HttpRequestMessage Request(HttpMethod method, string path, string tenant, string session)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Session", session);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new TenantBoundaryException(response.StatusCode == HttpStatusCode.Unauthorized ? "TENANT_SESSION_INVALID" :
                    response.StatusCode == HttpStatusCode.Forbidden ? "TENANT_ADMIN_REQUIRED" : "ACCESS_OWNER_UNAVAILABLE",
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? (int)response.StatusCode : 503);
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken: ct).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("ACCESS_OWNER_RESPONSE_INVALID", 503);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException)
        { throw new TenantBoundaryException("ACCESS_OWNER_UNAVAILABLE", 503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TenantBoundaryException("ACCESS_OWNER_UNAVAILABLE", 503); }
    }

    public void Dispose() => client.Dispose();
}
