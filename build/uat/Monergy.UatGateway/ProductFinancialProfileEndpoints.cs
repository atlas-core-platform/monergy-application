using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;

namespace Monergy.UatGateway;

public sealed record ProductFinancialPromotionRequest(
    string OperationId,
    IReadOnlyList<string> SourceFactIds);

public sealed record ProductFinancialSourceView(
    string SourceName,
    string DocumentVersion,
    DateTimeOffset RecordedAt,
    string TechnicalSourceFactId,
    string TechnicalProvenanceId);

public sealed record ProductFinancialFactView(
    string FinancialFactId,
    string FactType,
    string Label,
    decimal Value,
    string Currency,
    DateOnly EffectiveDate,
    int Revision,
    ProductFinancialSourceView Source);

public sealed record ProductFinancialProfileView(
    string FinancialProfileId,
    int Revision,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProductFinancialFactView> Facts,
    string DataDurability);

public sealed record ProductFinancialPromotionResult(
    ProductFinancialProfileView Profile,
    int Confirmed);

public sealed record ProductFinancialFactDetailView(
    ProductFinancialFactView Current,
    IReadOnlyList<ProductFinancialFactView> History);

public static class ProductFinancialProfileEndpoints
{
    private static readonly HashSet<string> SupportedFactTypes = new(StringComparer.Ordinal)
    { "INCOME", "EXPENSE" };

    public static void AddProductFinancialProfile(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;
        builder.Services.AddSingleton<ProductFinancialProfileClient>();
    }

    public static void MapProductFinancialProfile(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Monergy:Product:Enabled")) return;

        app.MapGet("/product-api/v1/financial-profile", (
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductFinancialProfileClient financial,
            ProductEvidenceClient evidence) =>
            InvokeAsync(async () =>
            {
                var session = await owners.SessionAsync(http).ConfigureAwait(false);
                var customer = await owners.OwnedCustomerAsync(
                    session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                    ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);

                var read = await access.EvaluateCustomerAsync(
                    http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
                if (read.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

                return await ReadProductProfileAsync(
                    http, session, customer, read, access, financial, evidence).ConfigureAwait(false);
            }));

        app.MapGet("/product-api/v1/financial-profile/facts/{financialFactId}", (
            string financialFactId,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductFinancialProfileClient financial,
            ProductEvidenceClient evidence) =>
            InvokeAsync(async () =>
            {
                if (!TenantAccessClient.Identifier(financialFactId, 160))
                    throw new TenantBoundaryException("FINANCIAL_FACT_INVALID", 400);

                var session = await owners.SessionAsync(http).ConfigureAwait(false);
                var customer = await owners.OwnedCustomerAsync(
                    session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                    ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);

                var read = await access.EvaluateCustomerAsync(
                    http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
                if (read.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

                var evidenceDecision = await access.EvaluateCustomerAsync(
                    http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
                if (evidenceDecision.Outcome != "Allow")
                    throw new TenantBoundaryException("ACCESS_DENIED", 403);

                var names = await ReadEvidenceNamesAsync(
                    session, customer, evidenceDecision, evidence, http.RequestAborted).ConfigureAwait(false);

                var current = await ReadFinancialFactAsync(
                    financialFactId, null, session, customer, read, financial, names, http.RequestAborted)
                    .ConfigureAwait(false)
                    ?? throw new TenantBoundaryException("FINANCIAL_FACT_NOT_FOUND", 404);

                var history = new List<ProductFinancialFactView>(current.Revision);
                for (var revision = current.Revision; revision >= 1; revision--)
                {
                    var item = await ReadFinancialFactAsync(
                        financialFactId, revision, session, customer, read, financial, names, http.RequestAborted)
                        .ConfigureAwait(false)
                        ?? throw new TenantBoundaryException("FINANCIAL_FACT_HISTORY_INCOMPLETE", 503);
                    history.Add(item);
                }

                return new ProductFinancialFactDetailView(current, history);
            }));

        app.MapPost("/product-api/v1/financial-profile/from-processing/{jobId}", (
            string jobId,
            ProductFinancialPromotionRequest submission,
            HttpContext http,
            CustomerOwnerClient owners,
            TenantAccessClient access,
            ProductDocumentProcessingClient processing,
            ProductFinancialProfileClient financial,
            ProductEvidenceClient evidence) =>
            InvokeAsync(async () =>
            {
                Validate(jobId, submission);

                var session = await owners.SessionAsync(http).ConfigureAwait(false);
                var customer = await owners.OwnedCustomerAsync(
                    session.TenantId, session.ActorId, http.RequestAborted).ConfigureAwait(false)
                    ?? throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);

                var update = await access.EvaluateCustomerAsync(
                    http, "financial-profile.profile.update", customer.CustomerId).ConfigureAwait(false);
                if (update.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);
                // This workspace is customer-owned. The Access owner evaluates current Consent
                // when access is delegated; self-access is intentionally SELF_CONSENT_NOT_REQUIRED.
                if (update.AccessBasis != "Self")
                    throw new TenantBoundaryException("PRODUCT_CUSTOMER_REQUIRED", 403);

                var process = await access.EvaluateCustomerAsync(
                    http, "document-intelligence.document.process", customer.CustomerId).ConfigureAwait(false);
                if (process.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

                var statusRequest = new ContractRequest<GetJobStatus>(
                    Vs02ContractNames.GetJobStatus,
                    ContractGuard.CurrentVersion,
                    "request-financial-review-status-" + Guid.NewGuid().ToString("N"),
                    "correlation-financial-review-" + submission.OperationId,
                    null,
                    Security(session, customer.CustomerId, process, null),
                    null,
                    new GetJobStatus(jobId, customer.CustomerId));
                var reviewed = await processing.StatusAsync(
                    session.TenantId, statusRequest, http.RequestAborted).ConfigureAwait(false);

                if (reviewed.Job.State != JobState.Completed ||
                    reviewed.Processing is not { State: ProcessingState.Completed } ||
                    reviewed.Facts.Count == 0)
                    throw new TenantBoundaryException("FINANCIAL_REVIEW_NOT_READY", 409);

                var selectedIds = submission.SourceFactIds.ToHashSet(StringComparer.Ordinal);
                var selected = reviewed.Facts
                    .Where(fact => selectedIds.Contains(fact.SourceFactId))
                    .ToArray();

                if (selected.Length != selectedIds.Count ||
                    selected.Any(fact => !SupportedFactTypes.Contains(fact.FactType) ||
                        !string.Equals(fact.CustomerId, customer.CustomerId, StringComparison.Ordinal)))
                    throw new TenantBoundaryException("FINANCIAL_REVIEW_SELECTION_INVALID", 400);

                if (selected.GroupBy(
                        fact => fact.FactType + "|" + fact.Label,
                        StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new TenantBoundaryException("FINANCIAL_REVIEW_DUPLICATE_FACT", 409);

                var now = DateTimeOffset.UtcNow;
                var request = new ContractRequest<NormalizeSourceFacts>(
                    Vs02ContractNames.NormalizeSourceFacts,
                    ContractGuard.CurrentVersion,
                    "request-financial-review-" + submission.OperationId,
                    "correlation-financial-review-" + submission.OperationId,
                    jobId,
                    Security(session, customer.CustomerId, update, null),
                    submission.OperationId,
                    new NormalizeSourceFacts(
                        ProfileId(customer.CustomerId),
                        customer.CustomerId,
                        reviewed.Processing.ProcessingId,
                        selected,
                        "reviewed-document-facts/1.0.0",
                        now));

                var normalized = await financial.NormalizeAsync(
                    request, http.RequestAborted).ConfigureAwait(false);
                if (normalized.Outcome != ContractOutcome.Success || normalized.Data is null)
                {
                    var conflict = normalized.Error?.Category == ContractErrorCategory.Conflict;
                    throw new TenantBoundaryException(
                        conflict ? "FINANCIAL_PROFILE_CONFLICT" : "FINANCIAL_PROFILE_UPDATE_REJECTED",
                        conflict ? 409 : 400);
                }

                var read = await access.EvaluateCustomerAsync(
                    http, "financial-profile.profile.read", customer.CustomerId).ConfigureAwait(false);
                if (read.Outcome != "Allow") throw new TenantBoundaryException("ACCESS_DENIED", 403);

                var profile = await ReadProductProfileAsync(
                    http, session, customer, read, access, financial, evidence).ConfigureAwait(false);
                return new ProductFinancialPromotionResult(
                    profile,
                    normalized.Data.Facts.Count);
            }));
    }

    private static async Task<ProductFinancialProfileView> ReadProductProfileAsync(
        HttpContext http,
        CustomerOwnerSession session,
        CustomerOwnerWorkspace customer,
        CustomerAccessDecision profileDecision,
        TenantAccessClient access,
        ProductFinancialProfileClient financial,
        ProductEvidenceClient evidence)
    {
        var request = new ContractRequest<GetFinancialProfile>(
            Vs02ContractNames.GetFinancialProfile,
            ContractGuard.CurrentVersion,
            "request-financial-profile-" + Guid.NewGuid().ToString("N"),
            "correlation-financial-profile-" + Guid.NewGuid().ToString("N"),
            null,
            Security(session, customer.CustomerId, profileDecision, null),
            null,
            new GetFinancialProfile(ProfileId(customer.CustomerId), customer.CustomerId));

        var profileResult = await financial.ProfileAsync(
            request, http.RequestAborted).ConfigureAwait(false);
        if (profileResult.Outcome == ContractOutcome.Rejected &&
            profileResult.Error?.Category == ContractErrorCategory.NotFound)
            return new ProductFinancialProfileView(
                ProfileId(customer.CustomerId), 0, null, [], "Temporary data");

        if (profileResult.Outcome != ContractOutcome.Success || profileResult.Data is null)
            throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_UNAVAILABLE", 503);

        var evidenceDecision = await access.EvaluateCustomerAsync(
            http, "evidence.document.read", customer.CustomerId).ConfigureAwait(false);
        if (evidenceDecision.Outcome != "Allow")
            throw new TenantBoundaryException("ACCESS_DENIED", 403);

        var names = await ReadEvidenceNamesAsync(
            session, customer, evidenceDecision, evidence, http.RequestAborted).ConfigureAwait(false);

        var facts = new List<ProductFinancialFactView>();
        foreach (var fact in profileResult.Data.Facts.OrderBy(
                     fact => fact.FactType, StringComparer.Ordinal).ThenBy(
                     fact => fact.Label, StringComparer.Ordinal))
        {
            var provenanceRequest = new ContractRequest<GetFinancialProvenance>(
                Vs02ContractNames.GetFinancialProvenance,
                ContractGuard.CurrentVersion,
                "request-financial-source-" + Guid.NewGuid().ToString("N"),
                "correlation-financial-source-" + Guid.NewGuid().ToString("N"),
                null,
                Security(session, customer.CustomerId, profileDecision, null),
                null,
                new GetFinancialProvenance(fact.FinancialProvenanceId, customer.CustomerId));
            var provenance = await financial.ProvenanceAsync(
                provenanceRequest, http.RequestAborted).ConfigureAwait(false);
            if (provenance.Outcome != ContractOutcome.Success || provenance.Data is null)
                throw new TenantBoundaryException("FINANCIAL_PROFILE_PROVENANCE_UNAVAILABLE", 503);

            facts.Add(new ProductFinancialFactView(
                fact.FinancialFactId,
                fact.FactType,
                fact.Label,
                fact.Value,
                fact.Currency,
                fact.EffectiveDate,
                fact.Revision,
                new ProductFinancialSourceView(
                    names.GetValueOrDefault(provenance.Data.DocumentVersionId, "Uploaded document"),
                    provenance.Data.DocumentVersionId,
                    provenance.Data.RecordedAt,
                    provenance.Data.SourceFactId,
                    provenance.Data.FinancialProvenanceId)));
        }

        return new ProductFinancialProfileView(
            profileResult.Data.FinancialProfileId,
            profileResult.Data.Revision,
            profileResult.Data.UpdatedAt,
            facts,
            "Temporary data");
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadEvidenceNamesAsync(
        CustomerOwnerSession session,
        CustomerOwnerWorkspace customer,
        CustomerAccessDecision evidenceDecision,
        ProductEvidenceClient evidence,
        CancellationToken ct)
    {
        var libraryRequest = new ContractRequest<ListCustomerEvidence>(
            App02ContractNames.ListCustomerEvidence,
            ContractGuard.CurrentVersion,
            "request-financial-sources-" + Guid.NewGuid().ToString("N"),
            "correlation-financial-sources-" + Guid.NewGuid().ToString("N"),
            null,
            Security(session, customer.CustomerId, evidenceDecision, null),
            null,
            new ListCustomerEvidence(customer.CustomerId, 100));
        var library = await evidence.ListAsync(
            session.TenantId,
            session.AuthenticationContextId,
            libraryRequest,
            ct).ConfigureAwait(false);
        return library.Items.ToDictionary(
            item => item.DocumentVersionId,
            item => item.OriginalFileName,
            StringComparer.Ordinal);
    }

    private static async Task<ProductFinancialFactView?> ReadFinancialFactAsync(
        string financialFactId,
        int? revision,
        CustomerOwnerSession session,
        CustomerOwnerWorkspace customer,
        CustomerAccessDecision profileDecision,
        ProductFinancialProfileClient financial,
        IReadOnlyDictionary<string, string> sourceNames,
        CancellationToken ct)
    {
        var factRequest = new ContractRequest<GetFinancialFact>(
            Vs02ContractNames.GetFinancialFact,
            ContractGuard.CurrentVersion,
            "request-financial-fact-" + Guid.NewGuid().ToString("N"),
            "correlation-financial-fact-" + Guid.NewGuid().ToString("N"),
            null,
            Security(session, customer.CustomerId, profileDecision, null),
            null,
            new GetFinancialFact(financialFactId, customer.CustomerId, revision));
        var factResult = await financial.FactAsync(factRequest, ct).ConfigureAwait(false);
        if (factResult.Outcome == ContractOutcome.Rejected &&
            factResult.Error?.Category == ContractErrorCategory.NotFound)
            return null;
        if (factResult.Outcome != ContractOutcome.Success || factResult.Data is null)
            throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_UNAVAILABLE", 503);

        var provenanceRequest = new ContractRequest<GetFinancialProvenance>(
            Vs02ContractNames.GetFinancialProvenance,
            ContractGuard.CurrentVersion,
            "request-financial-source-" + Guid.NewGuid().ToString("N"),
            "correlation-financial-source-" + Guid.NewGuid().ToString("N"),
            null,
            Security(session, customer.CustomerId, profileDecision, null),
            null,
            new GetFinancialProvenance(factResult.Data.FinancialProvenanceId, customer.CustomerId));
        var provenance = await financial.ProvenanceAsync(provenanceRequest, ct).ConfigureAwait(false);
        if (provenance.Outcome != ContractOutcome.Success || provenance.Data is null)
            throw new TenantBoundaryException("FINANCIAL_PROFILE_PROVENANCE_UNAVAILABLE", 503);

        return new ProductFinancialFactView(
            factResult.Data.FinancialFactId,
            factResult.Data.FactType,
            factResult.Data.Label,
            factResult.Data.Value,
            factResult.Data.Currency,
            factResult.Data.EffectiveDate,
            factResult.Data.Revision,
            new ProductFinancialSourceView(
                sourceNames.GetValueOrDefault(provenance.Data.DocumentVersionId, "Uploaded document"),
                provenance.Data.DocumentVersionId,
                provenance.Data.RecordedAt,
                provenance.Data.SourceFactId,
                provenance.Data.FinancialProvenanceId));
    }

    private static TrustedSecurityContext Security(
        CustomerOwnerSession session,
        string customerId,
        CustomerAccessDecision decision,
        string? consentReferenceId) => new(
        new ActorContext(session.ActorId, "Customer", DateTimeOffset.UtcNow, session.AuthenticationContextId),
        new WorkloadContext("customer-web", "local-uat-workspace:" + session.TenantId),
        new AccessContext("customer-advice", consentReferenceId, decision.DecisionId, customerId));

    private static string ProfileId(string customerId) => "financial-profile-" + customerId;

    private static void Validate(string jobId, ProductFinancialPromotionRequest submission)
    {
        if (!TenantAccessClient.Identifier(jobId, 160) ||
            !jobId.StartsWith("job-processing-", StringComparison.Ordinal))
            throw new TenantBoundaryException("PROCESSING_JOB_INVALID", 400);
        if (submission.OperationId.Length != 32 ||
            submission.OperationId.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new TenantBoundaryException("FINANCIAL_OPERATION_INVALID", 400);
        if (submission.SourceFactIds is null ||
            submission.SourceFactIds.Count is < 1 or > 50 ||
            submission.SourceFactIds.Distinct(StringComparer.Ordinal).Count() != submission.SourceFactIds.Count ||
            submission.SourceFactIds.Any(id => !TenantAccessClient.Identifier(id, 160)))
            throw new TenantBoundaryException("FINANCIAL_REVIEW_SELECTION_INVALID", 400);
    }

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (TenantBoundaryException failure)
        { return Results.Json(new { error = failure.Code }, statusCode: failure.Status); }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or NotSupportedException)
        { return Results.Json(new { error = "FINANCIAL_PROFILE_OWNER_UNAVAILABLE" }, statusCode: 503); }
        catch (OperationCanceledException)
        { return Results.Json(new { error = "FINANCIAL_PROFILE_OWNER_UNAVAILABLE" }, statusCode: 503); }
    }
}

public sealed class ProductFinancialProfileClient : IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1_048_576 };
    private readonly Uri endpoint;

    public ProductFinancialProfileClient(IConfiguration configuration)
    {
        endpoint = TenantAccessIntegration.LoopbackRoot(
            TenantAccessIntegration.Required(configuration, "Monergy:Product:FinancialProfileUrl"));
    }

    public Task<ContractResult<FinancialProfileDetails>> ProfileAsync(
        ContractRequest<GetFinancialProfile> request,
        CancellationToken ct) =>
        SendAsync<GetFinancialProfile, FinancialProfileDetails>("contracts/cid-030/v1", request, ct);

    public Task<ContractResult<NormalizationResult>> NormalizeAsync(
        ContractRequest<NormalizeSourceFacts> request,
        CancellationToken ct) =>
        SendAsync<NormalizeSourceFacts, NormalizationResult>("contracts/cid-031/v1", request, ct);

    public Task<ContractResult<AuthoritativeFinancialFact>> FactAsync(
        ContractRequest<GetFinancialFact> request,
        CancellationToken ct) =>
        SendAsync<GetFinancialFact, AuthoritativeFinancialFact>("contracts/cid-032/v1", request, ct);

    public Task<ContractResult<FinancialProvenance>> ProvenanceAsync(
        ContractRequest<GetFinancialProvenance> request,
        CancellationToken ct) =>
        SendAsync<GetFinancialProvenance, FinancialProvenance>("contracts/cid-033/v1", request, ct);

    private async Task<ContractResult<TData>> SendAsync<TPayload, TData>(
        string path,
        ContractRequest<TPayload> request,
        CancellationToken ct)
    {
        try
        {
            var tenant = LocalTenantSecurityContext.RequireTenant(
                request.Security.Workload?.WorkloadIdentityId,
                null,
                "local-uat-workspace");
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
            {
                Content = JsonContent.Create(request, options: ContractJson.Options),
            };
            message.Headers.Add("X-Monergy-Tenant", tenant);
            message.Headers.Add("X-Monergy-Session", request.Security.Actor.AuthenticationContextId);
            using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TenantBoundaryException("TENANT_SESSION_INVALID", 401);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new TenantBoundaryException("ACCESS_DENIED", 403);
            if ((int)response.StatusCode >= 500 ||
                (int)response.StatusCode is >= 300 and < 400)
                throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_UNAVAILABLE", 503);

            var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                ContractJson.Options, ct).ConfigureAwait(false)
                ?? throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_RESPONSE_INVALID", 503);
            if (!string.Equals(result.ContractName, request.ContractName, StringComparison.Ordinal) ||
                !string.Equals(result.ContractVersion, request.ContractVersion, StringComparison.Ordinal) ||
                !string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal) ||
                !string.Equals(result.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
                throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_RESPONSE_INVALID", 503);
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
        { throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_UNAVAILABLE", 503); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TenantBoundaryException("FINANCIAL_PROFILE_OWNER_UNAVAILABLE", 503); }
    }

    public void Dispose() => client.Dispose();
}

