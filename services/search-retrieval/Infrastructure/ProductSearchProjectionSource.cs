using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monergy.Contracts;
using Monergy.Platform;
using Monergy.Services.SearchRetrieval.Application;
using Monergy.Services.SearchRetrieval.Domain;

namespace Monergy.Services.SearchRetrieval.Infrastructure;

public sealed class ProductSearchProjectionSource(
    IHttpClientFactory clients,
    IHttpContextAccessor accessor,
    IConfiguration configuration) : ISearchProjectionSource, ICustomerSearchProjectionSource
{
    public const string EvidenceClient = "product-search-evidence";
    public const string FinancialProfileClient = "product-search-financial-profile";
    private static readonly HashSet<string> SupportedFactTypes = new(StringComparer.Ordinal)
    { "INCOME", "EXPENSE", "LOAN", "ASSET", "INVESTMENT" };
    private readonly object sync = new();
    private ImmutableArray<DerivedSearchRecord> records = [];
    private readonly Uri evidenceEndpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:EvidenceUrl"));
    private readonly Uri financialEndpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:FinancialProfileUrl"));

    public static bool Selected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:ProductSearch:Enabled");

    public ImmutableArray<DerivedSearchRecord> ReadAll()
    {
        lock (sync) return records;
    }

    public ImmutableArray<DerivedSearchRecord> ReadForCustomer(string customerId)
    {
        lock (sync) return records.Where(item => item.CustomerId == customerId).ToImmutableArray();
    }

    public async Task PrepareCustomerAsync(
        string customerId,
        string authorizationContextId,
        TrustedSecurityContext security,
        string operation,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var proof = accessor.HttpContext?.Features.Get<TenantBoundaryProof>();
        if (proof is null ||
            proof.CapabilityId != "search.query.execute" ||
            proof.ContractName != operation ||
            proof.CustomerId != customerId ||
            proof.ActorId != security.Actor.ActorId ||
            proof.SessionId != security.Actor.AuthenticationContextId ||
            !string.Equals(authorizationContextId, security.Access.AuthorizationContextId, StringComparison.Ordinal))
        {
            throw Failure("search.source.boundary-invalid", ContractErrorCategory.AccessDenied,
                "Current search authorization is required.", correlationId);
        }

        var sourceSecurity = security with
        {
            Workload = new WorkloadContext("search-retrieval", "local-uat-search-retrieval:" + proof.TenantId),
        };

        var refreshed = operation switch
        {
            SearchContractNames.SearchDocuments => await ReadDocumentsAsync(
                proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken)
                .ConfigureAwait(false),
            SearchContractNames.SearchFinancialData => await ReadFinancialAsync(
                proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken)
                .ConfigureAwait(false),
            _ => throw Failure("search.source.operation-invalid", ContractErrorCategory.UnsupportedOperation,
                "The product search source does not support this operation.", correlationId),
        };

        var refreshedType = operation == SearchContractNames.SearchDocuments
            ? SearchResultType.Document
            : SearchResultType.Financial;
        lock (sync)
        {
            records = records.Where(item =>
                    item.CustomerId != customerId || item.ResultType != refreshedType)
                .Concat(refreshed)
                .OrderBy(item => item.SearchRecordId, StringComparer.Ordinal)
                .ToImmutableArray();
        }
    }

    private async Task<ImmutableArray<DerivedSearchRecord>> ReadDocumentsAsync(
        TenantBoundaryProof proof,
        string customerId,
        string authorizationContextId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var request = new ContractRequest<ListCustomerEvidence>(
            App02ContractNames.ListCustomerEvidence,
            ContractGuard.CurrentVersion,
            RequestId("documents"),
            correlationId,
            null,
            security,
            null,
            new ListCustomerEvidence(customerId, 100));
        var result = await SendAsync<ListCustomerEvidence, CustomerEvidenceLibrary>(
            clients.CreateClient(EvidenceClient),
            evidenceEndpoint,
            proof,
            "contracts/app-02/evidence-list/v1",
            request,
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome != ContractOutcome.Success || result.Data is null)
            throw OwnerFailure(result.Error, "search.source.evidence-unavailable", correlationId);
        if (result.Data.CustomerId != customerId ||
            result.Data.Items.Any(item =>
                string.IsNullOrWhiteSpace(item.DocumentVersionId) ||
                string.IsNullOrWhiteSpace(item.EvidenceId) ||
                string.IsNullOrWhiteSpace(item.OriginalFileName)))
            throw Failure("search.source.evidence-boundary-invalid", ContractErrorCategory.AccessDenied,
                "Document search sources are outside the current customer boundary.", correlationId);

        return result.Data.Items
            .OrderBy(item => item.OriginalFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DocumentVersionId, StringComparer.Ordinal)
            .Select(item => new DerivedSearchRecord(
                "search-document-" + item.DocumentVersionId,
                customerId,
                authorizationContextId,
                SearchResultType.Document,
                item.OriginalFileName,
                $"{item.SourceName} version {item.Version}",
                SearchAuthority.EvidenceService,
                new SearchSourceReference(
                    "DocumentVersion",
                    item.DocumentVersionId,
                    item.EvidenceId,
                    null,
                    null)))
            .ToImmutableArray();
    }

    private async Task<ImmutableArray<DerivedSearchRecord>> ReadFinancialAsync(
        TenantBoundaryProof proof,
        string customerId,
        string authorizationContextId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var profileId = "financial-profile-" + customerId;
        var profileRequest = new ContractRequest<GetFinancialProfile>(
            Vs02ContractNames.GetFinancialProfile,
            ContractGuard.CurrentVersion,
            RequestId("financial-profile"),
            correlationId,
            null,
            security,
            null,
            new GetFinancialProfile(profileId, customerId));
        var profileResult = await SendAsync<GetFinancialProfile, FinancialProfileDetails>(
            clients.CreateClient(FinancialProfileClient),
            financialEndpoint,
            proof,
            "contracts/cid-030/v1",
            profileRequest,
            cancellationToken).ConfigureAwait(false);

        if (profileResult.Outcome == ContractOutcome.Rejected &&
            profileResult.Error?.Category == ContractErrorCategory.NotFound)
            return [];
        if (profileResult.Outcome != ContractOutcome.Success || profileResult.Data is null)
            throw OwnerFailure(profileResult.Error, "search.source.financial-profile-unavailable", correlationId);

        var profile = profileResult.Data;
        if (profile.CustomerId != customerId || profile.FinancialProfileId != profileId)
            throw Failure("search.source.financial-boundary-invalid", ContractErrorCategory.AccessDenied,
                "Financial search sources are outside the current customer boundary.", correlationId);

        var builder = ImmutableArray.CreateBuilder<DerivedSearchRecord>();
        foreach (var fact in profile.Facts
                     .Where(fact => SupportedFactTypes.Contains(fact.FactType))
                     .OrderBy(fact => fact.FactType, StringComparer.Ordinal)
                     .ThenBy(fact => fact.Label, StringComparer.Ordinal))
        {
            if (fact.CustomerId != customerId || fact.FinancialProfileId != profileId)
                throw Failure("search.source.financial-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "Financial search sources are outside the current customer boundary.", correlationId);

            var provenanceRequest = new ContractRequest<GetFinancialProvenance>(
                Vs02ContractNames.GetFinancialProvenance,
                ContractGuard.CurrentVersion,
                RequestId("financial-provenance"),
                correlationId,
                null,
                security,
                null,
                new GetFinancialProvenance(fact.FinancialProvenanceId, customerId));
            var provenanceResult = await SendAsync<GetFinancialProvenance, FinancialProvenance>(
                clients.CreateClient(FinancialProfileClient),
                financialEndpoint,
                proof,
                "contracts/cid-033/v1",
                provenanceRequest,
                cancellationToken).ConfigureAwait(false);
            if (provenanceResult.Outcome != ContractOutcome.Success || provenanceResult.Data is null)
                throw OwnerFailure(provenanceResult.Error, "search.source.financial-profile-unavailable", correlationId);
            var provenance = provenanceResult.Data;
            if (provenance.CustomerId != customerId ||
                provenance.FinancialFactId != fact.FinancialFactId ||
                provenance.FinancialProvenanceId != fact.FinancialProvenanceId)
                throw Failure("search.source.financial-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "Financial provenance is outside the current customer boundary.", correlationId);

            builder.Add(new DerivedSearchRecord(
                "search-financial-" + fact.FinancialFactId,
                customerId,
                authorizationContextId,
                SearchResultType.Financial,
                fact.Label,
                $"{(fact.FactType == "INCOME" ? "Income" : fact.FactType == "EXPENSE" ? "Expense" : fact.FactType == "LOAN" ? "Loan source amount (not outstanding balance)" : fact.FactType == "ASSET" ? "Asset source figure (not verified valuation)" : "Investment source figure (not live market value)")} {fact.Value} {fact.Currency} effective {fact.EffectiveDate:yyyy-MM-dd}",
                SearchAuthority.FinancialProfileService,
                new SearchSourceReference(
                    "FinancialFact",
                    fact.FinancialFactId,
                    profileId,
                    fact.FinancialProvenanceId,
                    null)));
        }
        return builder.ToImmutable();
    }

    private static async Task<ContractResult<TData>> SendAsync<TPayload, TData>(
        HttpClient client,
        Uri endpoint,
        TenantBoundaryProof proof,
        string path,
        ContractRequest<TPayload> request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
        {
            Content = JsonContent.Create(request, options: ContractJson.Options),
        };
        message.Headers.Add("X-Monergy-Tenant", proof.TenantId);
        message.Headers.Add("X-Monergy-Session", proof.SessionId);

        try
        {
            using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw Failure("search.source.owner-denied", ContractErrorCategory.AccessDenied,
                    "Current source authorization is required.", request.CorrelationId);
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode is >= 300 and < 400)
                throw Failure("search.source.owner-unavailable", ContractErrorCategory.DependencyFailure,
                    "A search source is unavailable.", request.CorrelationId, true);
            var result = await response.Content.ReadFromJsonAsync<ContractResult<TData>>(
                ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw Failure("search.source.response-malformed", ContractErrorCategory.DependencyFailure,
                    "A search source response is invalid.", request.CorrelationId);
            if (result.ContractName != request.ContractName ||
                result.ContractVersion != request.ContractVersion ||
                result.RequestId != request.RequestId ||
                result.CorrelationId != request.CorrelationId)
                throw Failure("search.source.response-malformed", ContractErrorCategory.DependencyFailure,
                    "A search source response is invalid.", request.CorrelationId);
            return result;
        }
        catch (JsonException)
        {
            throw Failure("search.source.response-malformed", ContractErrorCategory.DependencyFailure,
                "A search source response is invalid.", request.CorrelationId);
        }
        catch (HttpRequestException)
        {
            throw Failure("search.source.owner-unavailable", ContractErrorCategory.DependencyFailure,
                "A search source is unavailable.", request.CorrelationId, true);
        }
    }

    private static string RequestId(string kind) =>
        "search-source-" + kind + "-" + Guid.NewGuid().ToString("N");

    private static SearchSourceException OwnerFailure(
        ContractError? error,
        string fallbackCode,
        string correlationId) =>
        error is null
            ? Failure(fallbackCode, ContractErrorCategory.DependencyFailure,
                "A search source is unavailable.", correlationId, true)
            : new(error with { CorrelationId = correlationId });

    private static SearchSourceException Failure(
        string code,
        ContractErrorCategory category,
        string message,
        string correlationId,
        bool retryable = false) =>
        new(new ContractError(code, category, message, retryable, correlationId));
}
