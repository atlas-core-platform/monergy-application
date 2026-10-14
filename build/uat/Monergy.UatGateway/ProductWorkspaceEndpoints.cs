using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductWorkspaceContext(
    string TenantId,
    string ActorId,
    string CustomerId,
    string CustomerName,
    string? CustomerDetail,
    IReadOnlyList<string> Capabilities,
    string Environment,
    string DataDurability,
    bool ProductionAccepted);

public sealed record ProductEvidenceSubmission(
    string OperationId,
    string OriginalFileName,
    string ContentType,
    string ContentBase64);

public sealed record ProductEvidenceReceipt(
    string OperationId,
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    int Version,
    string OriginalFileName,
    string ContentType,
    long ContentLength,
    string ContentSha256,
    DateTimeOffset AcceptedAt,
    string DataDurability);

public static class ProductWorkspaceEndpoints
{
    private static readonly string[] ProductCapabilities =
    [
        "evidence.document.upload",
        "financial-profile.profile.read",
        "search.query.execute",
        "reporting.report.generate",
    ];
    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "text/plain" };

    public static void AddProductWorkspace(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        builder.Services.AddSingleton<CustomerOwnerClient>();
        builder.Services.AddSingleton<ProductEvidenceClient>();
    }

    public static void MapProductWorkspace(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        app.MapGet("/product-api/v1/context", async (
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access) =>
        {
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var capabilities = new List<string>();
            foreach (var capability in ProductCapabilities)
            {
                var decision = await access.EvaluateCustomerAsync(http, capability, customer.CustomerId).ConfigureAwait(false);
                if (decision.Outcome == "Allow") capabilities.Add(capability);
            }
            return Results.Ok(new ProductWorkspaceContext(
                session.TenantId,
                session.ActorId,
                customer.CustomerId,
                customer.DisplayName,
                customer.SecondaryLabel,
                capabilities,
                "Local UAT",
                "Runtime only",
                false));
        });
        app.MapPost("/product-api/v1/evidence", async (
            ProductEvidenceSubmission submission,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductEvidenceClient evidence) =>
        {
            Validate(submission);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(http, "evidence.document.upload", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

            byte[] content;
            try { content = Convert.FromBase64String(submission.ContentBase64); }
            catch (FormatException) { throw new TenantBoundaryException("EVIDENCE_CONTENT_INVALID", 400); }
            if (content.Length is < 1 or > 1_048_576)
                throw new TenantBoundaryException("EVIDENCE_CONTENT_SIZE_INVALID", 400);

            var staged = await evidence.StageAsync(submission.OperationId, submission.ContentBase64,
                submission.ContentType, http.RequestAborted).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var documentId = "document-" + submission.OperationId;
            var documentVersionId = "document-version-" + submission.OperationId;
            var evidenceId = "evidence-" + submission.OperationId;
            var request = new ContractRequest<CreateDocumentVersion>(
                Vs02ContractNames.CreateDocumentVersion,
                ContractGuard.CurrentVersion,
                "request-" + submission.OperationId,
                "correlation-" + submission.OperationId,
                null,
                new TrustedSecurityContext(
                    new ActorContext(session.ActorId, "Customer", now, session.AuthenticationContextId),
                    new WorkloadContext("customer-web", "local-uat-workspace"),
                    new AccessContext("customer-advice", null, decision.DecisionId, customer.CustomerId)),
                submission.OperationId,
                new CreateDocumentVersion(
                    documentId,
                    documentVersionId,
                    evidenceId,
                    customer.CustomerId,
                    "Customer upload",
                    submission.OriginalFileName,
                    submission.ContentType,
                    staged.Reference,
                    staged.Sha256,
                    now));
            var registration = await evidence.RegisterAsync(
                session.TenantId, session.AuthenticationContextId, request, http.RequestAborted).ConfigureAwait(false);
            if (registration.Outcome != ContractOutcome.Success || registration.Data is null)
                throw new TenantBoundaryException(registration.Error?.Code.ToUpperInvariant().Replace('.', '_')
                    ?? "EVIDENCE_REGISTRATION_FAILED", registration.Error?.Category switch
                    {
                        ContractErrorCategory.AccessDenied or ContractErrorCategory.AuthenticationRequired => 403,
                        ContractErrorCategory.Conflict => 409,
                        ContractErrorCategory.DependencyFailure or ContractErrorCategory.TemporarilyUnavailable => 503,
                        _ => 400,
                    });
            return Results.Ok(new ProductEvidenceReceipt(
                submission.OperationId,
                registration.Data.DocumentId,
                registration.Data.DocumentVersionId,
                registration.Data.EvidenceId,
                registration.Data.Version,
                submission.OriginalFileName,
                submission.ContentType,
                staged.ContentLength,
                registration.Data.ContentSha256,
                registration.Data.CreatedAt,
                "Runtime only"));
        });
    }

    private static void Validate(ProductEvidenceSubmission submission)
    {
        if (submission.OperationId.Length != 32 || submission.OperationId.Any(character =>
            !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new TenantBoundaryException("EVIDENCE_OPERATION_INVALID", 400);
        if (string.IsNullOrWhiteSpace(submission.OriginalFileName) || submission.OriginalFileName.Length > 180 ||
            submission.OriginalFileName.Contains('/') || submission.OriginalFileName.Contains('\\'))
            throw new TenantBoundaryException("EVIDENCE_FILE_NAME_INVALID", 400);
        if (!SupportedContentTypes.Contains(submission.ContentType))
            throw new TenantBoundaryException("EVIDENCE_CONTENT_TYPE_UNSUPPORTED", 400);
        if (string.IsNullOrWhiteSpace(submission.ContentBase64) || submission.ContentBase64.Length > 1_398_104)
            throw new TenantBoundaryException("EVIDENCE_CONTENT_SIZE_INVALID", 400);
    }
}

public sealed class ProductEvidenceClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;
    private readonly string token;

    public ProductEvidenceClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:EvidenceUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:Product:EvidenceToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<EvidenceContentDescriptor> StageAsync(
        string operationId,
        string contentBase64,
        string contentType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "internal/local/v1/evidence/content"));
        request.Headers.Add("X-Monergy-Product-Token", token);
        request.Content = JsonContent.Create(new { operationId, contentBase64, contentType });
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(response.StatusCode == System.Net.HttpStatusCode.Conflict
                ? "EVIDENCE_OPERATION_CONFLICT" : "EVIDENCE_STAGING_FAILED",
                response.StatusCode == System.Net.HttpStatusCode.Conflict ? 409 : response.StatusCode == System.Net.HttpStatusCode.BadRequest ? 400 : 503);
        EvidenceContentDescriptor descriptor;
        try
        {
            descriptor = await response.Content.ReadFromJsonAsync<EvidenceContentDescriptor>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("EVIDENCE_STAGING_RESPONSE_INVALID", 503);
        }
        catch (JsonException) { throw new TenantBoundaryException("EVIDENCE_STAGING_RESPONSE_INVALID", 503); }
        if (!descriptor.Reference.StartsWith("reference://customer-intake/", StringComparison.Ordinal) ||
            descriptor.Sha256.Length != 64 || descriptor.ContentLength is < 1 or > 1_048_576)
            throw new TenantBoundaryException("EVIDENCE_STAGING_RESPONSE_INVALID", 503);
        return descriptor;
    }

    public async Task<ContractResult<DocumentVersionCreatedResult>> RegisterAsync(
        string tenantId,
        string sessionId,
        ContractRequest<CreateDocumentVersion> body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "contracts/cid-020/v1"));
        request.Headers.Add("X-Monergy-Tenant", tenantId);
        request.Headers.Add("X-Monergy-Session", sessionId);
        request.Content = JsonContent.Create(body, options: ContractJson.Options);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 500 or >= 300 and < 400 or 404)
            throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);
        if ((int)response.StatusCode is 401 or 403)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        try
        {
            return await response.Content.ReadFromJsonAsync<ContractResult<DocumentVersionCreatedResult>>(ContractJson.Options,
                cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
        }
        catch (JsonException) { throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503); }
    }

    public void Dispose() => client.Dispose();

    public sealed record EvidenceContentDescriptor(string Reference, string Sha256, string ContentType, long ContentLength);
}
