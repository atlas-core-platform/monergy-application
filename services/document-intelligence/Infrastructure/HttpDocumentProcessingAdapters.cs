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
    string Content,
    string? ContentBase64 = null);

public sealed class HttpEvidenceContentReader : IEvidenceContentReader, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_500_000 };
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
                throw new EvidenceContentUnsupportedException("This evidence format is unsupported by the LOCAL processor.");
            if (!response.IsSuccessStatusCode)
                throw new EvidenceDependencyException("The Evidence owner did not supply the requested version.");
            var source = await response.Content.ReadFromJsonAsync<EvidenceProcessingSource>(Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new EvidenceDependencyException("The Evidence owner returned an invalid response.");
            byte[] bytes;
            var binary = source.ContentType is "image/png" or "image/jpeg" or "application/pdf";
            try
            {
                if (binary && (source.Content.Length != 0 || source.ContentBase64 is null || source.ContentBase64.Length > 1_398_104) ||
                    !binary && source.ContentBase64 is not null)
                    throw new EvidenceDependencyException("The Evidence owner returned conflicting source content.");
                bytes = binary ? Convert.FromBase64String(source.ContentBase64!) : Encoding.UTF8.GetBytes(source.Content);
            }
            catch (FormatException) { throw new EvidenceDependencyException("The Evidence owner returned invalid source bytes."); }
            if (source.DocumentVersionId != documentVersionId || source.CustomerId != customerId ||
                (source.ContentType != "text/plain" && source.ContentType != "text/csv" && !binary) ||
                bytes.Length is < 1 or > 1_048_576 || source.ContentSha256.Length != 64 ||
                !string.Equals(source.ContentSha256,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    StringComparison.OrdinalIgnoreCase))
                throw new EvidenceDependencyException("The Evidence owner returned a mismatched source version.");
            return new EvidenceContent(new EvidenceReference(
                source.EvidenceId,
                source.DocumentId,
                source.DocumentVersionId,
                source.CustomerId,
                source.ContentReference,
                source.ContentSha256,
                source.ContentType), source.Content, binary ? bytes : null);
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
