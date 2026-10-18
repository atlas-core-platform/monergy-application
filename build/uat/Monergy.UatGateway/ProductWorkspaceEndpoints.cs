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

public sealed record ProductEvidenceLibrary(
    IReadOnlyList<CustomerEvidenceItem> Items,
    string DataDurability);

public sealed record ProductEvidenceSourceView(
    string DocumentId,
    string DocumentVersionId,
    string EvidenceId,
    int Version,
    string SourceName,
    string OriginalFileName,
    string ContentType,
    string ContentSha256,
    DateTimeOffset ReceivedAt,
    string DataDurability);

public sealed record ProductEvidenceVersionHistory(
    string DocumentId,
    IReadOnlyList<EvidenceVersionMetadata> Versions,
    string DataDurability);

public sealed record ProductDocumentProcessingRequest(string OperationId);
public sealed record ProductDocumentProcessingView(
    ScheduledJob Job,
    ProcessingStatus? Processing,
    IReadOnlyList<ValidatedSourceFact> Facts,
    string DataDurability,
    string ProcessingMode);

public sealed record ProductProcessingActivityItem(
    string JobId,
    string DocumentVersionId,
    JobState State,
    int Attempt,
    DateTimeOffset UpdatedAt);

public sealed record ProductProcessingActivityView(
    IReadOnlyList<ProductProcessingActivityItem> Items,
    string DataDurability);

public sealed record ProductProcessingResultFact(
    string SourceFactId,
    string FactType,
    string Label,
    decimal CandidateValue,
    string Currency,
    DateOnly EffectiveDate,
    string SourceLocation,
    decimal Confidence,
    string DocumentVersionId);

public sealed record ProductProcessingResultView(
    string JobId,
    string DocumentVersionId,
    JobState State,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductProcessingResultFact> Facts,
    string DataDurability);

public static class ProductWorkspaceEndpoints
{
    private static readonly string[] ProductCapabilities =
    [
        "evidence.document.upload",
        "evidence.document.read",
        "document-intelligence.document.process",
        "job-management.job.read",
        "financial-profile.profile.read",
        "financial-profile.profile.update",
        "search.query.execute",
        "reporting.report.generate",
        "reporting.report.read",
    ];
    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "text/plain", "text/csv" };

    public static void AddProductWorkspace(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        // The gateway's shared HttpClient is also used by readiness and proxy endpoints.
        // Product owner calls retain their stricter timeout, response bound and no-proxy handler.
        builder.Services.AddSingleton(provider => new CustomerOwnerClient(
            provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<ProductEvidenceClient>();
        builder.Services.AddSingleton<ProductDocumentProcessingClient>();
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
                "Test environment",
                "Temporary data",
                false));
        });
        app.MapGet("/product-api/v1/evidence", async (
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductEvidenceClient evidence) =>
        {
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

            var now = DateTimeOffset.UtcNow;
            var requestId = "request-evidence-library-" + Guid.NewGuid().ToString("N");
            var request = new ContractRequest<ListCustomerEvidence>(
                App02ContractNames.ListCustomerEvidence,
                ContractGuard.CurrentVersion,
                requestId,
                "correlation-evidence-library-" + Guid.NewGuid().ToString("N"),
                null,
                new TrustedSecurityContext(
                    new ActorContext(session.ActorId, "Customer", now, session.AuthenticationContextId),
                    new WorkloadContext("customer-web", "local-uat-workspace"),
                    new AccessContext("customer-advice", null, decision.DecisionId, customer.CustomerId)),
                null,
                new ListCustomerEvidence(customer.CustomerId, 100));
            var library = await evidence.ListAsync(
                session.TenantId, session.AuthenticationContextId, request, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new ProductEvidenceLibrary(library.Items, "Temporary data"));
        });
        app.MapGet("/product-api/v1/evidence/{documentId}/versions", async (
            string documentId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductEvidenceClient evidence) =>
        {
            if (!TenantAccessClient.Identifier(documentId, 160))
                throw new TenantBoundaryException("DOCUMENT_ID_INVALID", 400);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var security = Security(session, customer.CustomerId, decision);

            var request = new ContractRequest<GetEvidenceMetadata>(
                Vs02ContractNames.GetEvidenceMetadata,
                ContractGuard.CurrentVersion,
                "request-evidence-history-" + Guid.NewGuid().ToString("N"),
                "correlation-evidence-history-" + Guid.NewGuid().ToString("N"),
                null,
                security,
                null,
                new GetEvidenceMetadata(documentId, customer.CustomerId));
            var result = await evidence.MetadataAsync(
                session.TenantId, session.AuthenticationContextId, request, http.RequestAborted)
                .ConfigureAwait(false);

            if (result.Outcome == ContractOutcome.Rejected &&
                result.Error?.Category == ContractErrorCategory.NotFound)
                throw new TenantBoundaryException("EVIDENCE_DOCUMENT_NOT_FOUND", 404);
            if (result.Outcome != ContractOutcome.Success || result.Data is null)
                throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);

            var metadata = result.Data;
            if (metadata.DocumentId != documentId ||
                metadata.CustomerId != customer.CustomerId ||
                metadata.Versions.Count is < 1 or > 100 ||
                metadata.Versions.Select(version => version.DocumentVersionId)
                    .Distinct(StringComparer.Ordinal).Count() != metadata.Versions.Count ||
                metadata.Versions.Select(version => version.Version).Distinct().Count() != metadata.Versions.Count ||
                metadata.Versions.Any(version => !ProductEvidenceClient.ValidVersionMetadata(version)))
                throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);

            var ordered = metadata.Versions
                .OrderByDescending(version => version.Version)
                .ThenByDescending(version => version.ReceivedAt)
                .ThenBy(version => version.DocumentVersionId, StringComparer.Ordinal)
                .ToArray();

            return Results.Ok(new ProductEvidenceVersionHistory(
                documentId,
                ordered,
                "Temporary data"));
        });

        app.MapGet("/product-api/v1/evidence/source/{documentVersionId}", async (
            string documentVersionId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductEvidenceClient evidence) =>
        {
            if (!TenantAccessClient.Identifier(documentVersionId, 160))
                throw new TenantBoundaryException("DOCUMENT_VERSION_INVALID", 400);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var security = Security(session, customer.CustomerId, decision);

            var referenceRequest = new ContractRequest<GetEvidenceReference>(
                Vs02ContractNames.GetEvidenceReference,
                ContractGuard.CurrentVersion,
                "request-evidence-source-" + Guid.NewGuid().ToString("N"),
                "correlation-evidence-source-" + Guid.NewGuid().ToString("N"),
                null,
                security,
                null,
                new GetEvidenceReference(documentVersionId, customer.CustomerId));
            var reference = await evidence.ReferenceAsync(
                session.TenantId, session.AuthenticationContextId, referenceRequest, http.RequestAborted)
                .ConfigureAwait(false);
            if (reference.Outcome == ContractOutcome.Rejected &&
                reference.Error?.Category == ContractErrorCategory.NotFound)
                throw new TenantBoundaryException("EVIDENCE_VERSION_NOT_FOUND", 404);
            if (reference.Outcome != ContractOutcome.Success || reference.Data is null)
                throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);

            var metadataRequest = new ContractRequest<GetEvidenceMetadata>(
                Vs02ContractNames.GetEvidenceMetadata,
                ContractGuard.CurrentVersion,
                "request-evidence-source-metadata-" + Guid.NewGuid().ToString("N"),
                "correlation-evidence-source-metadata-" + Guid.NewGuid().ToString("N"),
                null,
                security,
                null,
                new GetEvidenceMetadata(reference.Data.DocumentId, customer.CustomerId));
            var metadata = await evidence.MetadataAsync(
                session.TenantId, session.AuthenticationContextId, metadataRequest, http.RequestAborted)
                .ConfigureAwait(false);
            if (metadata.Outcome != ContractOutcome.Success || metadata.Data is null)
                throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);
            var version = metadata.Data.Versions.SingleOrDefault(
                item => item.DocumentVersionId == documentVersionId)
                ?? throw new TenantBoundaryException("EVIDENCE_VERSION_NOT_FOUND", 404);

            return Results.Ok(new ProductEvidenceSourceView(
                reference.Data.DocumentId,
                reference.Data.DocumentVersionId,
                reference.Data.EvidenceId,
                version.Version,
                version.SourceName,
                version.OriginalFileName,
                version.ContentType,
                version.ContentSha256,
                version.ReceivedAt,
                "Temporary data"));
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
                "Temporary data"));
        });
        app.MapPost("/product-api/v1/evidence/{documentVersionId}/processing", async (
            string documentVersionId,
            ProductDocumentProcessingRequest submission,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductEvidenceClient evidence,
            ProductDocumentProcessingClient processing) =>
        {
            ValidateOperation(submission.OperationId);
            if (!TenantAccessClient.Identifier(documentVersionId, 160))
                throw new TenantBoundaryException("DOCUMENT_VERSION_INVALID", 400);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "document-intelligence.document.process", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var security = Security(session, customer.CustomerId, decision);
            var listRequest = new ContractRequest<ListCustomerEvidence>(
                App02ContractNames.ListCustomerEvidence,
                ContractGuard.CurrentVersion,
                "request-processing-source-" + submission.OperationId,
                "correlation-processing-" + submission.OperationId,
                null,
                security,
                null,
                new ListCustomerEvidence(customer.CustomerId, 100));
            var library = await evidence.ListAsync(
                session.TenantId, session.AuthenticationContextId, listRequest, http.RequestAborted).ConfigureAwait(false);
            var source = library.Items.SingleOrDefault(item => item.DocumentVersionId == documentVersionId)
                ?? throw new TenantBoundaryException("EVIDENCE_VERSION_NOT_FOUND", 404);
            if (!string.Equals(source.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source.ContentType, "text/csv", StringComparison.OrdinalIgnoreCase))
                throw new TenantBoundaryException("PROCESSING_CONTENT_TYPE_UNSUPPORTED", 422);
            var now = DateTimeOffset.UtcNow;
            var jobId = "job-processing-" + submission.OperationId;
            var processingId = "processing-" + submission.OperationId;
            var jobRequest = new ContractRequest<ScheduleJob>(
                Vs02ContractNames.ScheduleJob,
                ContractGuard.CurrentVersion,
                "request-job-" + submission.OperationId,
                "correlation-processing-" + submission.OperationId,
                null,
                security,
                submission.OperationId,
                new ScheduleJob(
                    jobId,
                    "document-processing",
                    "Document Intelligence Service",
                    source.DocumentVersionId,
                    customer.CustomerId,
                    now));
            var job = await processing.ScheduleAsync(
                session.TenantId,
                new ScheduleDocumentProcessingJob(jobRequest, processingId, source.EvidenceId),
                http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new ProductDocumentProcessingView(
                job,
                null,
                [],
                "Temporary data",
                "Structured text reference processor"));
        });
        app.MapGet("/product-api/v1/processing-activity", async (
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductDocumentProcessingClient processing) =>
        {
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "job-management.job.read", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var security = Security(session, customer.CustomerId, decision);

            var items = await processing.ActivityAsync(
                session.TenantId,
                security,
                customer.CustomerId,
                20,
                http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(new ProductProcessingActivityView(
                items.Select(job => new ProductProcessingActivityItem(
                    job.JobId,
                    job.PayloadReference,
                    job.State,
                    job.Attempt,
                    job.UpdatedAt)).ToArray(),
                "Temporary data"));
        });

        app.MapGet("/product-api/v1/processing-result/{jobId}", async (
            string jobId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductDocumentProcessingClient processing) =>
        {
            if (!TenantAccessClient.Identifier(jobId, 160) ||
                !jobId.StartsWith("job-processing-", StringComparison.Ordinal))
                throw new TenantBoundaryException("PROCESSING_JOB_INVALID", 400);

            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(
                session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var jobDecision = await access.EvaluateCustomerAsync(
                http, "job-management.job.read", customer.CustomerId).ConfigureAwait(false);
            var evidenceDecision = await access.EvaluateCustomerAsync(
                http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
            if (jobDecision.Outcome != "Allow" || evidenceDecision.Outcome != "Allow")
                throw new TenantBoundaryException("ACCESS_DENIED", 403);

            var security = Security(session, customer.CustomerId, jobDecision);
            var result = await processing.ReadCompletedResultAsync(
                session.TenantId,
                security,
                jobId,
                customer.CustomerId,
                http.RequestAborted).ConfigureAwait(false);
            if (result.Result.Facts.Count is < 1 or > 100 ||
                result.Result.Facts.Select(fact => fact.SourceFactId)
                    .Distinct(StringComparer.Ordinal).Count() != result.Result.Facts.Count ||
                result.Result.Facts.Any(fact =>
                    fact.FactType is not ("INCOME" or "EXPENSE" or "LOAN" or "ASSET" or "INVESTMENT" or "INSURANCE" or "TAX") ||
                    !TenantAccessClient.Identifier(fact.SourceFactId, 160) ||
                    string.IsNullOrWhiteSpace(fact.Label) || fact.Label.Length > 160 ||
                    string.IsNullOrWhiteSpace(fact.Currency) || fact.Currency.Length is < 3 or > 16 ||
                    string.IsNullOrWhiteSpace(fact.SourceLocation) || fact.SourceLocation.Length > 160 ||
                    fact.Confidence is < 0m or > 1m))
                throw new TenantBoundaryException("PROCESSING_RESULT_INVALID", 503);

            return Results.Ok(new ProductProcessingResultView(
                result.Job.JobId,
                result.Job.PayloadReference,
                result.Job.State,
                result.Result.UpdatedAt,
                result.Result.Facts.Select(fact => new ProductProcessingResultFact(
                    fact.SourceFactId,
                    fact.FactType,
                    fact.Label,
                    fact.CandidateValue,
                    fact.Currency,
                    fact.EffectiveDate,
                    fact.SourceLocation,
                    fact.Confidence,
                    fact.DocumentVersionId)).ToArray(),
                "Temporary data"));
        });

        app.MapGet("/product-api/v1/processing/{jobId}", async (
            string jobId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductDocumentProcessingClient processing) =>
        {
            if (!TenantAccessClient.Identifier(jobId, 160) || !jobId.StartsWith("job-processing-", StringComparison.Ordinal))
                throw new TenantBoundaryException("PROCESSING_JOB_INVALID", 400);
            var session = await owners.SessionAsync(http).ConfigureAwait(false);
            var customer = await owners.OwnedCustomerAsync(session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);
            var decision = await access.EvaluateCustomerAsync(
                http, "document-intelligence.document.process", customer.CustomerId).ConfigureAwait(false);
            if (decision.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
            var request = new ContractRequest<GetJobStatus>(
                Vs02ContractNames.GetJobStatus,
                ContractGuard.CurrentVersion,
                "request-status-" + Guid.NewGuid().ToString("N"),
                "correlation-status-" + Guid.NewGuid().ToString("N"),
                null,
                Security(session, customer.CustomerId, decision),
                null,
                new GetJobStatus(jobId, customer.CustomerId));
            var view = await processing.StatusAsync(session.TenantId, request, http.RequestAborted).ConfigureAwait(false);
            return Results.Ok(view with { DataDurability = "Temporary data", ProcessingMode = "Structured text reference processor" });
        });
    }

    private static TrustedSecurityContext Security(
        CustomerOwnerSession session,
        string customerId,
        CustomerAccessDecision decision) => new(
        new ActorContext(session.ActorId, "Customer", DateTimeOffset.UtcNow, session.AuthenticationContextId),
        new WorkloadContext("customer-web", "local-uat-workspace:" + session.TenantId),
        new AccessContext("customer-advice", null, decision.DecisionId, customerId));

    private static void ValidateOperation(string operationId)
    {
        if (operationId.Length != 32 || operationId.Any(character =>
            !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new TenantBoundaryException("PROCESSING_OPERATION_INVALID", 400);
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

public sealed record ScheduleDocumentProcessingJob(
    ContractRequest<ScheduleJob> Job,
    string ProcessingId,
    string EvidenceReferenceId);

public sealed class ProductDocumentProcessingClient : IDisposable
{
    private sealed record JobProcessingView(
        ScheduledJob Job,
        ProcessingStatus? Processing,
        IReadOnlyList<ValidatedSourceFact> Facts);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;
    private readonly string token;

    public ProductDocumentProcessingClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:JobManagementUrl"));
        token = TenantAccessIntegration.Required(configuration, "Monergy:Product:JobManagementToken");
        TenantAccessIntegration.ValidateToken(token);
    }

    public async Task<ScheduledJob> ScheduleAsync(
        string tenantId,
        ScheduleDocumentProcessingJob input,
        CancellationToken cancellationToken)
    {
        using var request = Message(
            HttpMethod.Post, "internal/local/v1/jobs/document-processing", tenantId, input);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(response.StatusCode == System.Net.HttpStatusCode.Conflict
                ? "PROCESSING_OPERATION_CONFLICT" : response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "ACCESS_DENIED" : "JOB_OWNER_UNAVAILABLE",
                response.StatusCode == System.Net.HttpStatusCode.Conflict ? 409 :
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 503);
        var result = await response.Content.ReadFromJsonAsync<ContractResult<ScheduledJob>>(
            ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        if (result.Outcome != ContractOutcome.Success || result.Data is null ||
            result.Data.JobId != input.Job.Payload.JobId || result.Data.CustomerId != input.Job.Payload.CustomerId)
            throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        return result.Data;
    }

    private sealed record ListDocumentProcessingJobsRequest(
        TrustedSecurityContext Security,
        string CustomerId,
        int Limit);

    private sealed record DocumentProcessingActivityView(
        IReadOnlyList<ScheduledJob> Items);

    private sealed record ReadDocumentProcessingJobResultRequest(
        TrustedSecurityContext Security,
        string JobId,
        string CustomerId);

    public sealed record DocumentProcessingReadOnlyServiceView(
        string ProcessingId,
        string DocumentVersionId,
        ProcessingState State,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<ValidatedSourceFact> Facts);

    public sealed record DocumentProcessingJobReadOnlyView(
        ScheduledJob Job,
        DocumentProcessingReadOnlyServiceView Result);

    public async Task<IReadOnlyList<ScheduledJob>> ActivityAsync(
        string tenantId,
        TrustedSecurityContext security,
        string customerId,
        int limit,
        CancellationToken cancellationToken)
    {
        using var request = Message(
            HttpMethod.Post,
            "internal/local/v1/jobs/document-processing/list",
            tenantId,
            new ListDocumentProcessingJobsRequest(security, customerId, limit));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "ACCESS_DENIED" : "JOB_OWNER_UNAVAILABLE",
                response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 503);
        var view = await response.Content.ReadFromJsonAsync<DocumentProcessingActivityView>(
            Json, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        if (view.Items.Count > limit ||
            view.Items.Select(job => job.JobId).Distinct(StringComparer.Ordinal).Count() != view.Items.Count ||
            view.Items.Zip(view.Items.Skip(1)).Any(pair => pair.First.UpdatedAt < pair.Second.UpdatedAt) ||
            view.Items.Any(job =>
                job.CustomerId != customerId ||
                job.JobName != "document-processing" ||
                !job.JobId.StartsWith("job-processing-", StringComparison.Ordinal) ||
                !TenantAccessClient.Identifier(job.PayloadReference, 160)))
            throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        return view.Items;
    }

    public async Task<DocumentProcessingJobReadOnlyView> ReadCompletedResultAsync(
        string tenantId,
        TrustedSecurityContext security,
        string jobId,
        string customerId,
        CancellationToken cancellationToken)
    {
        using var request = Message(
            HttpMethod.Post,
            "internal/local/v1/jobs/document-processing/read",
            tenantId,
            new ReadDocumentProcessingJobResultRequest(security, jobId, customerId));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "ACCESS_DENIED",
                System.Net.HttpStatusCode.NotFound => "PROCESSING_RESULT_NOT_FOUND",
                System.Net.HttpStatusCode.Conflict => "PROCESSING_RESULT_NOT_READY",
                _ => "JOB_OWNER_UNAVAILABLE",
            }, response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => 403,
                System.Net.HttpStatusCode.NotFound => 404,
                System.Net.HttpStatusCode.Conflict => 409,
                _ => 503,
            });

        var view = await response.Content.ReadFromJsonAsync<DocumentProcessingJobReadOnlyView>(
            Json, cancellationToken).ConfigureAwait(false)
            ?? throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        if (view.Job.JobId != jobId ||
            view.Job.CustomerId != customerId ||
            view.Job.JobName != "document-processing" ||
            view.Job.State != JobState.Completed ||
            view.Result.State != ProcessingState.Completed ||
            view.Result.ProcessingId.Length is < 1 or > 160 ||
            view.Result.DocumentVersionId != view.Job.PayloadReference ||
            view.Result.Facts.Any(fact =>
                fact.CustomerId != customerId ||
                fact.DocumentVersionId != view.Job.PayloadReference))
            throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);

        return view;
    }

    public async Task<ProductDocumentProcessingView> StatusAsync(
        string tenantId,
        ContractRequest<GetJobStatus> input,
        CancellationToken cancellationToken)
    {
        using var request = Message(HttpMethod.Post, "internal/local/v1/jobs/status", tenantId, input);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TenantBoundaryException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "ACCESS_DENIED" : "JOB_OWNER_UNAVAILABLE",
                response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 503);
        var view = await response.Content.ReadFromJsonAsync<JobProcessingView>(Json, cancellationToken)
            .ConfigureAwait(false) ?? throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        if (view.Job.JobId != input.Payload.JobId || view.Job.CustomerId != input.Payload.CustomerId ||
            view.Facts.Any(fact => fact.CustomerId != input.Payload.CustomerId))
            throw new TenantBoundaryException("JOB_OWNER_RESPONSE_INVALID", 503);
        return new ProductDocumentProcessingView(
            view.Job,
            view.Processing,
            view.Facts,
            "Temporary data",
            "Structured text reference processor");
    }

    private HttpRequestMessage Message(HttpMethod method, string path, string tenantId, object body)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", tenantId);
        request.Headers.Add("X-Monergy-Job-Token", token);
        request.Content = JsonContent.Create(body, options: ContractJson.Options);
        return request;
    }

    public void Dispose() => client.Dispose();
}

public sealed class ProductEvidenceClient : IDisposable
{
    private static readonly HashSet<string> LibraryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "text/plain", "text/csv" };
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
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            throw new TenantBoundaryException("EVIDENCE_SCAN_REJECTED", 422);
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            throw new TenantBoundaryException("EVIDENCE_SCAN_UNAVAILABLE", 503);
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

    public async Task<CustomerEvidenceLibrary> ListAsync(
        string tenantId,
        string sessionId,
        ContractRequest<ListCustomerEvidence> body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "contracts/app-02/evidence-list/v1"));
        request.Headers.Add("X-Monergy-Tenant", tenantId);
        request.Headers.Add("X-Monergy-Session", sessionId);
        request.Content = JsonContent.Create(body, options: ContractJson.Options);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 500 or >= 300 and < 400 or 404)
            throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);
        if ((int)response.StatusCode is 401 or 403)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        ContractResult<CustomerEvidenceLibrary> result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<ContractResult<CustomerEvidenceLibrary>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
        }
        catch (JsonException) { throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503); }
        if (!string.Equals(result.ContractName, body.ContractName, StringComparison.Ordinal) ||
            !string.Equals(result.ContractVersion, body.ContractVersion, StringComparison.Ordinal) ||
            !string.Equals(result.RequestId, body.RequestId, StringComparison.Ordinal) ||
            !string.Equals(result.CorrelationId, body.CorrelationId, StringComparison.Ordinal) ||
            result.Outcome != ContractOutcome.Success || result.Data is null ||
            !string.Equals(result.Data.CustomerId, body.Payload.CustomerId, StringComparison.Ordinal) ||
            result.Data.Items.Count > body.Payload.Limit || result.Data.Items.Any(item => !ValidLibraryItem(item)))
            throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
        return result.Data;
    }

    public Task<ContractResult<EvidenceReference>> ReferenceAsync(
        string tenantId,
        string sessionId,
        ContractRequest<GetEvidenceReference> body,
        CancellationToken cancellationToken) =>
        SendContractAsync<GetEvidenceReference, EvidenceReference>(
            tenantId, sessionId, "contracts/cid-022/v1", body, cancellationToken);

    public Task<ContractResult<EvidenceMetadata>> MetadataAsync(
        string tenantId,
        string sessionId,
        ContractRequest<GetEvidenceMetadata> body,
        CancellationToken cancellationToken) =>
        SendContractAsync<GetEvidenceMetadata, EvidenceMetadata>(
            tenantId, sessionId, "contracts/cid-021/v1", body, cancellationToken);

    private async Task<ContractResult<TData>> SendContractAsync<TPayload, TData>(
        string tenantId,
        string sessionId,
        string path,
        ContractRequest<TPayload> body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", tenantId);
        request.Headers.Add("X-Monergy-Session", sessionId);
        request.Content = JsonContent.Create(body, options: ContractJson.Options);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
            throw new TenantBoundaryException("EVIDENCE_OWNER_UNAVAILABLE", 503);
        if ((int)response.StatusCode is 401 or 403)
            throw new TenantBoundaryException("ACCESS_DENIED", 403);
        try
        {
            var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
            if (!string.Equals(result.ContractName, body.ContractName, StringComparison.Ordinal) ||
                !string.Equals(result.ContractVersion, body.ContractVersion, StringComparison.Ordinal) ||
                !string.Equals(result.RequestId, body.RequestId, StringComparison.Ordinal) ||
                !string.Equals(result.CorrelationId, body.CorrelationId, StringComparison.Ordinal))
                throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
            return result;
        }
        catch (JsonException)
        {
            throw new TenantBoundaryException("EVIDENCE_OWNER_RESPONSE_INVALID", 503);
        }
    }

    public static bool ValidVersionMetadata(EvidenceVersionMetadata version) =>
        TenantAccessClient.Identifier(version.DocumentVersionId, 160) &&
        TenantAccessClient.Identifier(version.EvidenceId, 160) &&
        version.Version >= 1 &&
        !string.IsNullOrWhiteSpace(version.SourceName) && version.SourceName.Length <= 160 &&
        !string.IsNullOrWhiteSpace(version.OriginalFileName) && version.OriginalFileName.Length <= 180 &&
        !version.OriginalFileName.Contains('/') && !version.OriginalFileName.Contains('\\') &&
        LibraryContentTypes.Contains(version.ContentType) &&
        version.ContentSha256.Length == 64 && version.ContentSha256.All(char.IsAsciiHexDigit) &&
        version.ReceivedAt != default;

    private static bool ValidLibraryItem(CustomerEvidenceItem item) =>
        TenantAccessClient.Identifier(item.DocumentId, 160) &&
        TenantAccessClient.Identifier(item.DocumentVersionId, 160) &&
        TenantAccessClient.Identifier(item.EvidenceId, 160) &&
        item.Version >= 1 &&
        !string.IsNullOrWhiteSpace(item.SourceName) && item.SourceName.Length <= 160 &&
        !string.IsNullOrWhiteSpace(item.OriginalFileName) && item.OriginalFileName.Length <= 180 &&
        !item.OriginalFileName.Contains('/') && !item.OriginalFileName.Contains('\\') &&
        LibraryContentTypes.Contains(item.ContentType) && item.ContentLength >= 1 &&
        item.ContentSha256.Length == 64 && item.ContentSha256.All(char.IsAsciiHexDigit) &&
        item.ReceivedAt != default && item.CreatedAt != default;

    public void Dispose() => client.Dispose();

    public sealed record EvidenceContentDescriptor(string Reference, string Sha256, string ContentType, long ContentLength);
}
