using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.DocumentIntelligence.Application;

namespace Monergy.Services.DocumentIntelligence.Infrastructure;

public sealed record EvidenceProcessingSource(
    string EvidenceId,
    string DocumentId,
    string DocumentVersionId,
    string CustomerId,
    string ContentReference,
    string ContentSha256,
    string ContentType,
    string Content);

public sealed class HttpEvidenceContentReader : IEvidenceContentReader, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri evidence;
    private readonly string token;

    public HttpEvidenceContentReader(IConfiguration configuration)
    {
        evidence = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:EvidenceUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:EvidenceToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<EvidenceContent?> ReadAsync(
        string documentVersionId,
        string customerId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var tenant = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, null, "local-uat-workspace", "job-management");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(evidence, "internal/local/v1/evidence/processing-source"));
        request.Headers.Add("X-Monergy-Tenant", tenant);
        request.Headers.Add("X-Monergy-Processing-Token", token);
        request.Content = JsonContent.Create(new { documentVersionId, customerId });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                throw new EvidenceContentUnsupportedException("The LOCAL processor supports structured text and template CSV evidence only.");
            if (!response.IsSuccessStatusCode)
                throw new EvidenceDependencyException("The Evidence owner did not supply the requested version.");
            var source = await response.Content.ReadFromJsonAsync<EvidenceProcessingSource>(Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new EvidenceDependencyException("The Evidence owner returned an invalid response.");
            if (source.DocumentVersionId != documentVersionId || source.CustomerId != customerId ||
                (source.ContentType != "text/plain" && source.ContentType != "text/csv") || source.ContentSha256.Length != 64 ||
                !string.Equals(source.ContentSha256,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Content))),
                    StringComparison.OrdinalIgnoreCase))
                throw new EvidenceDependencyException("The Evidence owner returned a mismatched source version.");
            return new EvidenceContent(new EvidenceReference(
                source.EvidenceId,
                source.DocumentId,
                source.DocumentVersionId,
                source.CustomerId,
                source.ContentReference,
                source.ContentSha256,
                source.ContentType), source.Content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new EvidenceDependencyException("The Evidence owner timed out."); }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new EvidenceDependencyException("The Evidence owner is unavailable."); }
    }

    public void Dispose() => client.Dispose();
}

public sealed class OwnerBackedExecutionPolicy(TenantAccessClient access) : IExecutionPolicy
{
    public async Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext security,
        string customerId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = LocalTenantSecurityContext.RequireTenant(
                security.Workload?.WorkloadIdentityId, null, "local-uat-workspace", "job-management");
            var decision = await access.EvaluateCustomerAsync(
                tenant,
                security.Actor.AuthenticationContextId,
                "document-intelligence.document.process",
                customerId,
                cancellationToken).ConfigureAwait(false);
            if (decision.Outcome != "Allow" || decision.ActorId != security.Actor.ActorId)
                return Denied(correlationId);
            return null;
        }
        catch (TenantBoundaryException failure)
        {
            return new ContractError(
                "processing.authorization.unavailable",
                failure.Status == 403 ? ContractErrorCategory.AccessDenied : ContractErrorCategory.DependencyFailure,
                "Current server-side execution authorization could not be confirmed.",
                failure.Status >= 500,
                correlationId);
        }
    }

    private static ContractError Denied(string correlationId) => new(
        "processing.authorization.denied",
        ContractErrorCategory.AccessDenied,
        "Current server-side execution authorization is required.",
        false,
        correlationId);
}
