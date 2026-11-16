using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Platform;
using Monergy.Services.ApplicationCase.Application;

namespace Monergy.Services.ApplicationCase;

public sealed record FinancialProfessionalConsentDecision(
    string TenantId,
    string ActorId,
    string CustomerId,
    string CapabilityId,
    string Purpose,
    bool Allowed,
    string Reason,
    long Version,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset? ExpiresAt);

public sealed class FinancialProfessionalConsentClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string token;

    public FinancialProfessionalConsentClient(IConfiguration configuration) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 16_384,
        })
    { }

    public FinancialProfessionalConsentClient(IConfiguration configuration, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (configuration["Monergy:ExecutionZone"] is not ("LOCAL" or "CI_EPHEMERAL"))
            throw new InvalidOperationException("Financial review Consent requires a selected LOCAL/CI transport.");
        endpoint = TenantAccessIntegration.LoopbackRoot(configuration["Monergy:ApplicationCase:ConsentUrl"]
            ?? throw new InvalidOperationException("Financial review Consent owner URL is required."));
        token = TenantAccessIntegration.Required(configuration, "Monergy:ApplicationCase:ConsentToken");
        TenantAccessIntegration.ValidateToken(token);
        client = httpClient;
    }

    public async Task<FinancialProfessionalConsentDecision> EvaluateAsync(
        string tenant, string actor, string customer, string capability, CancellationToken ct)
    {
        const string purpose = "customer-advice";
        if (!TenantAccessClient.Identifier(tenant, 64) || !TenantAccessClient.Identifier(actor, 128) ||
            !TenantAccessClient.Identifier(customer, 128) || !TenantAccessClient.Identifier(capability, 160))
            throw new ApplicationCaseException("FINANCIAL_CONSENT_REQUEST_INVALID", 400);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/v1/consent/evaluate"));
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Owner-Token", token);
        request.Content = JsonContent.Create(new
        {
            tenantId = tenant,
            actorId = actor,
            customerId = customer,
            capabilityId = capability,
            purpose,
        });
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ApplicationCaseException("FINANCIAL_CONSENT_UNAVAILABLE", 503);
            var result = await response.Content.ReadFromJsonAsync<FinancialProfessionalConsentDecision>(Json, ct)
                .ConfigureAwait(false) ?? throw new ApplicationCaseException("FINANCIAL_CONSENT_RESPONSE_INVALID", 503);
            if (result.TenantId != tenant || result.ActorId != actor || result.CustomerId != customer ||
                result.CapabilityId != capability || result.Purpose != purpose || result.Version < 1 ||
                result.EvaluatedAt < DateTimeOffset.UtcNow.AddSeconds(-10) ||
                result.EvaluatedAt > DateTimeOffset.UtcNow.AddSeconds(5))
                throw new ApplicationCaseException("FINANCIAL_CONSENT_RESPONSE_INVALID", 503);
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new ApplicationCaseException("FINANCIAL_CONSENT_UNAVAILABLE", 503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ApplicationCaseException("FINANCIAL_CONSENT_UNAVAILABLE", 503); }
    }

    public void Dispose() => client.Dispose();
}
