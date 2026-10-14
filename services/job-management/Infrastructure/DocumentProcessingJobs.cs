using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.JobManagement.Application;

namespace Monergy.Services.JobManagement.Infrastructure;

public sealed record DocumentProcessingJobIntent(
    string JobId,
    string ProcessingId,
    string DocumentVersionId,
    string EvidenceReferenceId,
    string CustomerId,
    string IdempotencyKey,
    DateTimeOffset RequestedAt);

public sealed class DocumentProcessingJobRegistry
{
    private readonly ConcurrentDictionary<string, DocumentProcessingJobIntent> intents = new(StringComparer.Ordinal);

    public DocumentProcessingJobIntent Register(DocumentProcessingJobIntent intent)
    {
        var current = intents.GetOrAdd(intent.JobId, intent);
        if (current with { RequestedAt = intent.RequestedAt } != intent)
            throw new InvalidOperationException("Document processing job intent conflicts.");
        return current;
    }

    public DocumentProcessingJobIntent? Get(string jobId) => intents.GetValueOrDefault(jobId);
}

public sealed class OwnerBackedJobExecutionPolicy(TenantAccessClient access) : IJobAuthorizationPolicy, IJobConsentDecisionPort
{
    public Task<ContractError?> AuthorizeAsync(
        TrustedSecurityContext context,
        DurableJobRecord job,
        string correlationId,
        CancellationToken cancellationToken) => EvaluateOwnerAsync(context, job, correlationId, cancellationToken);

    public Task<ContractError?> EvaluateAsync(
        TrustedSecurityContext context,
        DurableJobRecord job,
        string correlationId,
        CancellationToken cancellationToken) => EvaluateOwnerAsync(context, job, correlationId, cancellationToken);

    private async Task<ContractError?> EvaluateOwnerAsync(
        TrustedSecurityContext context,
        DurableJobRecord job,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = LocalTenantSecurityContext.RequireTenant(
                context.Workload?.WorkloadIdentityId, null, "local-uat-workspace");
            var decision = await access.EvaluateCustomerAsync(
                tenant,
                context.Actor.AuthenticationContextId,
                "document-intelligence.document.process",
                job.CustomerId,
                cancellationToken).ConfigureAwait(false);
            if (decision.Outcome != "Allow" || decision.ActorId != context.Actor.ActorId)
                return Denied(correlationId);
            return null;
        }
        catch (TenantBoundaryException failure)
        {
            return new ContractError(
                "job.authorization.unavailable",
                failure.Status == 403 ? ContractErrorCategory.AccessDenied : ContractErrorCategory.DependencyFailure,
                "Current server-side authorization could not be confirmed.",
                failure.Status >= 500,
                correlationId);
        }
    }

    private static ContractError Denied(string correlationId) => new(
        "job.authorization.denied",
        ContractErrorCategory.AccessDenied,
        "Current server-side authorization is required.",
        false,
        correlationId);
}

public sealed record DocumentProcessingServiceView(
    ProcessingStatus Status,
    IReadOnlyList<ValidatedSourceFact> Facts);

public sealed class HttpDocumentProcessingTarget : IJobExecutionTarget, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1_048_576 };
    private readonly DocumentProcessingJobRegistry registry;
    private readonly Uri endpoint;
    private readonly string token;

    public HttpDocumentProcessingTarget(IConfiguration configuration, DocumentProcessingJobRegistry registry)
    {
        this.registry = registry;
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:DocumentIntelligenceUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:DocumentProcessing:DocumentIntelligenceToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<JobExecutionResult> ExecuteAsync(DurableJobRecord job, CancellationToken cancellationToken)
    {
        var intent = registry.Get(job.JobId);
        if (intent is null || intent.CustomerId != job.CustomerId || intent.DocumentVersionId != job.PayloadReference)
            return JobExecutionResult.Failed("job.document-processing.intent-unavailable", false);
        var tenant = LocalTenantSecurityContext.RequireTenant(
            job.Security.Workload?.WorkloadIdentityId, null, "local-uat-workspace");
        var security = job.Security with
        {
            Workload = new WorkloadContext("job-management", "job-management:" + tenant),
        };
        var request = new ContractRequest<ProcessDocument>(
            Vs02ContractNames.ProcessDocument,
            ContractGuard.CurrentVersion,
            "request-" + intent.ProcessingId,
            job.CorrelationId,
            job.RequestId,
            security,
            intent.IdempotencyKey,
            new ProcessDocument(
                intent.ProcessingId,
                intent.DocumentVersionId,
                intent.EvidenceReferenceId,
                intent.CustomerId,
                intent.RequestedAt));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/local/v1/document-processing/execute"));
        message.Headers.Add("X-Monergy-Tenant", tenant);
        message.Headers.Add("X-Monergy-Document-Token", token);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return JobExecutionResult.Failed("job.document-processing.authorization-denied", false);
            if (!response.IsSuccessStatusCode)
                return JobExecutionResult.Failed("job.document-processing.owner-unavailable", (int)response.StatusCode >= 500);
            var value = await response.Content.ReadFromJsonAsync<DocumentProcessingServiceView>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (value is null || value.Status.ProcessingId != intent.ProcessingId ||
                value.Status.DocumentVersionId != intent.DocumentVersionId)
                return JobExecutionResult.Failed("job.document-processing.response-invalid", false);
            return value.Status.State == ProcessingState.Completed
                ? JobExecutionResult.Completed(intent.ProcessingId)
                : JobExecutionResult.Failed(value.Status.FailureCode ?? "job.document-processing.failed", value.Status.Retryable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return JobExecutionResult.Failed("job.document-processing.timeout", true); }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { return JobExecutionResult.Failed("job.document-processing.owner-unavailable", true); }
    }

    public async Task<DocumentProcessingServiceView?> GetStatusAsync(
        DurableJobRecord job,
        TrustedSecurityContext security,
        CancellationToken cancellationToken)
    {
        var intent = registry.Get(job.JobId);
        if (intent is null || intent.CustomerId != job.CustomerId || intent.DocumentVersionId != job.PayloadReference)
            return null;
        var tenant = LocalTenantSecurityContext.RequireTenant(
            security.Workload?.WorkloadIdentityId, null, "local-uat-workspace");
        var ownerSecurity = security with
        {
            Workload = new WorkloadContext("job-management", "job-management:" + tenant),
        };
        var request = new ContractRequest<GetProcessingStatus>(
            Vs02ContractNames.GetProcessingStatus,
            ContractGuard.CurrentVersion,
            "request-status-" + intent.ProcessingId,
            "correlation-status-" + Guid.NewGuid().ToString("N"),
            job.RequestId,
            ownerSecurity,
            null,
            new GetProcessingStatus(intent.ProcessingId, intent.CustomerId));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoint, "internal/local/v1/document-processing/status"));
        message.Headers.Add("X-Monergy-Tenant", tenant);
        message.Headers.Add("X-Monergy-Document-Token", token);
        message.Content = JsonContent.Create(request, options: ContractJson.Options);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new TenantBoundaryException(response.StatusCode == HttpStatusCode.Forbidden
                    ? "ACCESS_DENIED" : "DOCUMENT_PROCESSING_OWNER_UNAVAILABLE",
                    response.StatusCode == HttpStatusCode.Forbidden ? 403 : 503);
            return await response.Content.ReadFromJsonAsync<DocumentProcessingServiceView>(Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new TenantBoundaryException("DOCUMENT_PROCESSING_RESPONSE_INVALID", 503);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new TenantBoundaryException("DOCUMENT_PROCESSING_OWNER_UNAVAILABLE", 503); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TenantBoundaryException("DOCUMENT_PROCESSING_OWNER_UNAVAILABLE", 503); }
    }

    public void Dispose() => client.Dispose();
}
