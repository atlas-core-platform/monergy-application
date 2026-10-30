using System.Collections.Immutable;
using System.Globalization;
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
    { "INCOME", "EXPENSE", "LOAN", "ASSET", "INVESTMENT", "INSURANCE", "TAX" };
    private readonly Uri evidenceEndpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:EvidenceUrl"));
    private readonly Uri financialEndpoint = TenantAccessIntegration.LoopbackRoot(
        TenantAccessIntegration.Required(configuration, "Monergy:ProductSearch:FinancialProfileUrl"));

    public static bool Selected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Monergy:ProductSearch:Enabled");

    // Product projections exist only inside the authorized request that read them.
    public ImmutableArray<DerivedSearchRecord> ReadAll() => throw new InvalidOperationException("A current owner read is required.");
    public ImmutableArray<DerivedSearchRecord> ReadForCustomer(string customerId) => throw new InvalidOperationException("A current owner read is required.");

    public async Task<ImmutableArray<DerivedSearchRecord>> ReadCurrentCustomerAsync(
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

        return operation switch
        {
            SearchContractNames.SearchDocuments => await ReadDocumentsAsync(
                proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken)
                .ConfigureAwait(false),
            SearchContractNames.SearchFinancialData => await ReadFinancialAsync(
                proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken)
                .ConfigureAwait(false),
            SearchContractNames.BuildRetrievalContext or SearchContractNames.IndexRefreshRequested =>
                (await ReadDocumentsAsync(proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken).ConfigureAwait(false))
                .AddRange(await ReadFinancialAsync(proof, customerId, authorizationContextId, sourceSecurity, correlationId, cancellationToken).ConfigureAwait(false)),
            _ => throw Failure("search.source.operation-invalid", ContractErrorCategory.UnsupportedOperation,
                "The product search source does not support this operation.", correlationId),
        };

    }

    private async Task<ImmutableArray<DerivedSearchRecord>> ReadDocumentsAsync(
        TenantBoundaryProof proof,
        string customerId,
        string authorizationContextId,
        TrustedSecurityContext security,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var items = new List<CustomerEvidenceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Bound resource use without silently returning a truncated corpus.
            if (items.Count >= 10_000)
                throw Failure("search.source.corpus-limit", ContractErrorCategory.DependencyFailure,
                    "Document search exceeds the supported corpus size.", correlationId);
            var request = new ContractRequest<ListCustomerEvidence>(
                App02ContractNames.ListCustomerEvidence, ContractGuard.CurrentVersion,
                RequestId("documents"), correlationId, null, security, null,
                new ListCustomerEvidence(customerId, 100, after));
            var result = await SendAsync<ListCustomerEvidence, CustomerEvidenceLibrary>(
                clients.CreateClient(EvidenceClient), evidenceEndpoint, proof,
                "contracts/app-02/evidence-list/v1", request, cancellationToken).ConfigureAwait(false);
            if (result.Outcome != ContractOutcome.Success || result.Data is null)
                throw OwnerFailure(result.Error, "search.source.evidence-unavailable", correlationId);
            var page = result.Data;
            if (page.CustomerId != customerId || page.Items is null || page.Items.Count > 100 ||
                page.Items.Any(item => item is null ||
                    string.IsNullOrWhiteSpace(item.DocumentVersionId) ||
                    string.IsNullOrWhiteSpace(item.EvidenceId) ||
                    string.IsNullOrWhiteSpace(item.OriginalFileName) || !seen.Add(item.DocumentVersionId)) ||
                (page.NextVersionId is not null && (page.Items.Count != 100 ||
                    page.NextVersionId != page.Items[^1].DocumentVersionId || page.NextVersionId == after)))
                throw Failure("search.source.evidence-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "Document search sources are outside the current customer boundary.", correlationId);
            items.AddRange(page.Items);
            after = page.NextVersionId;
        } while (after is not null);

        return items
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
            .Select(fact => fact with { FactType = FinancialSourceFactBoundary.ForCustomer(fact.FactType) })
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
                $"{(fact.FactType == "INCOME" ? "Income" : fact.FactType == "EXPENSE" ? "Expense" : fact.FactType == "LOAN" ? "Loan source amount (not outstanding balance)" : fact.FactType == "ASSET" ? "Asset source figure (not verified valuation)" : fact.FactType == "INVESTMENT" ? "Investment source figure (not live market value)" : fact.FactType == "INSURANCE" ? "Insurance source figure (not verified coverage or claim benefit)" : "Tax source figure (not assessed liability or refund)")} {fact.Value} {fact.Currency} effective {fact.EffectiveDate:yyyy-MM-dd}",
                SearchAuthority.FinancialProfileService,
                new SearchSourceReference(
                    "FinancialFact",
                    fact.FinancialFactId,
                    profileId,
                    fact.FinancialProvenanceId,
                    null)));
        }

        foreach (var record in await ReadTaxRecordsAsync(
            proof, customerId, authorizationContextId, security, profileId,
            correlationId, cancellationToken).ConfigureAwait(false))
            builder.Add(record);

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<DerivedSearchRecord>> ReadTaxRecordsAsync(
        TenantBoundaryProof proof,
        string customerId,
        string authorizationContextId,
        TrustedSecurityContext security,
        string profileId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var list = await GetOwnerAsync<TaxRecordList>(
            clients.CreateClient(FinancialProfileClient), financialEndpoint, proof,
            "local/v1/customers/" + Uri.EscapeDataString(customerId) + "/tax-records",
            correlationId, cancellationToken).ConfigureAwait(false);
        if (list.Items is null || list.Items.Count > 200 || list.HasMore ||
            list.Items.Select(item => item?.TaxRecordId).Distinct(StringComparer.Ordinal).Count() != list.Items.Count)
            throw Failure("search.source.corpus-limit", ContractErrorCategory.DependencyFailure,
                "Reviewed tax record search exceeds the supported complete corpus.", correlationId);

        var builder = ImmutableArray.CreateBuilder<DerivedSearchRecord>(list.Items.Count);
        foreach (var record in list.Items.OrderBy(item => item.Tax.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TaxRecordId, StringComparer.Ordinal))
        {
            if (!Valid(record, customerId))
                throw Failure("search.source.tax-record-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "A reviewed tax record is outside the current customer boundary.", correlationId);

            var factRequest = new ContractRequest<GetFinancialFact>(
                Vs02ContractNames.GetFinancialFact, ContractGuard.CurrentVersion,
                RequestId("tax-record-source"), correlationId, null, security, null,
                new GetFinancialFact(record.Tax.SourceFinancialFactId, customerId, record.Tax.SourceRevision));
            var factResult = await SendAsync<GetFinancialFact, AuthoritativeFinancialFact>(
                clients.CreateClient(FinancialProfileClient), financialEndpoint, proof,
                "contracts/cid-032/v1", factRequest, cancellationToken).ConfigureAwait(false);
            if (factResult.Outcome != ContractOutcome.Success || factResult.Data is null)
                throw OwnerFailure(factResult.Error, "search.source.financial-profile-unavailable", correlationId);
            var fact = factResult.Data;
            if (fact.CustomerId != customerId || fact.FinancialProfileId != profileId ||
                fact.FinancialFactId != record.Tax.SourceFinancialFactId ||
                fact.Revision != record.Tax.SourceRevision ||
                FinancialSourceFactBoundary.ForCustomer(fact.FactType) != "TAX" ||
                fact.Currency != record.Currency || fact.FinancialProvenanceId != record.SourceProvenanceId)
                throw Failure("search.source.tax-record-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "A reviewed tax record source is outside the current customer boundary.", correlationId);

            var provenanceRequest = new ContractRequest<GetFinancialProvenance>(
                Vs02ContractNames.GetFinancialProvenance, ContractGuard.CurrentVersion,
                RequestId("tax-record-provenance"), correlationId, null, security, null,
                new GetFinancialProvenance(record.SourceProvenanceId, customerId));
            var provenanceResult = await SendAsync<GetFinancialProvenance, FinancialProvenance>(
                clients.CreateClient(FinancialProfileClient), financialEndpoint, proof,
                "contracts/cid-033/v1", provenanceRequest, cancellationToken).ConfigureAwait(false);
            if (provenanceResult.Outcome != ContractOutcome.Success || provenanceResult.Data is null)
                throw OwnerFailure(provenanceResult.Error, "search.source.financial-profile-unavailable", correlationId);
            var provenance = provenanceResult.Data;
            if (provenance.CustomerId != customerId || provenance.FinancialFactId != fact.FinancialFactId ||
                provenance.FinancialProvenanceId != record.SourceProvenanceId ||
                provenance.EvidenceId != record.EvidenceId || provenance.DocumentVersionId != record.DocumentVersionId)
                throw Failure("search.source.tax-record-boundary-invalid", ContractErrorCategory.AccessDenied,
                    "Reviewed tax record provenance is outside the current customer boundary.", correlationId);

            var amount = record.Tax.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            builder.Add(new DerivedSearchRecord(
                "search-financial-tax-record-" + record.TaxRecordId,
                customerId,
                authorizationContextId,
                SearchResultType.Financial,
                record.Tax.Name,
                $"Customer-reviewed tax record for {record.Tax.Jurisdiction}: {record.Tax.TaxType}; " +
                $"{record.Tax.AmountKind.ToLowerInvariant()} {amount} {record.Currency}; " +
                $"tax year {record.Tax.TaxYearLabel}; period {record.Tax.PeriodStart:yyyy-MM-dd} to {record.Tax.PeriodEnd:yyyy-MM-dd}. " +
                "This is not assessed tax liability, an accepted filing, payment confirmation, refund entitlement or tax advice.",
                SearchAuthority.FinancialProfileService,
                new SearchSourceReference(
                    "TaxRecord",
                    record.TaxRecordId,
                    record.Tax.SourceFinancialFactId,
                    record.SourceProvenanceId,
                    null)));
        }
        return builder.ToImmutable();
    }

    private static bool Valid(ReviewedTaxRecord? record, string customerId) =>
        record is { Tax: not null } && record.CustomerId == customerId &&
        TenantAccessClient.Identifier(record.TaxRecordId, 80) &&
        Guid.TryParseExact(record.OperationId, "N", out _) &&
        record.Revision > 0 && record.VerificationStatus == "CUSTOMER_REVIEWED" &&
        Text(record.Tax.Name) && Text(record.Tax.Jurisdiction) && Text(record.Tax.TaxType) &&
        Text(record.Tax.TaxYearLabel, 30) &&
        record.Tax.Amount is >= 0 and <= 999999999999.99m &&
        decimal.Round(record.Tax.Amount, 2) == record.Tax.Amount &&
        record.Tax.AmountKind is "PAID" or "DUE" or "WITHHELD" &&
        record.Tax.PeriodStart.Year >= 1900 && record.Tax.PeriodEnd >= record.Tax.PeriodStart &&
        TenantAccessClient.Identifier(record.Tax.SourceFinancialFactId, 160) &&
        record.Tax.SourceRevision > 0 && record.Currency.Length == 3 &&
        record.Currency.All(char.IsAsciiLetterUpper) &&
        TenantAccessClient.Identifier(record.SourceProvenanceId, 160) &&
        TenantAccessClient.Identifier(record.DocumentVersionId, 160) &&
        TenantAccessClient.Identifier(record.EvidenceId, 160) &&
        TenantAccessClient.Identifier(record.RecordedBy, 160) && record.RecordedAt != default;

    private static bool Text(string? value, int maximum = 120) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum && !value.Any(char.IsControl);

    private static async Task<T> GetOwnerAsync<T>(
        HttpClient client,
        Uri endpoint,
        TenantBoundaryProof proof,
        string path,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Add("X-Monergy-Tenant", proof.TenantId);
        request.Headers.Add("X-Monergy-Session", proof.SessionId);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw Failure("search.source.owner-denied", ContractErrorCategory.AccessDenied,
                    "Current source authorization is required.", correlationId);
            if (!response.IsSuccessStatusCode)
                throw Failure("search.source.owner-unavailable", ContractErrorCategory.DependencyFailure,
                    "A search source is unavailable.", correlationId, true);
            return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken)
                .ConfigureAwait(false) ?? throw Failure("search.source.response-malformed",
                    ContractErrorCategory.DependencyFailure, "A search source response is invalid.", correlationId);
        }
        catch (JsonException)
        {
            throw Failure("search.source.response-malformed", ContractErrorCategory.DependencyFailure,
                "A search source response is invalid.", correlationId);
        }
        catch (HttpRequestException)
        {
            throw Failure("search.source.owner-unavailable", ContractErrorCategory.DependencyFailure,
                "A search source is unavailable.", correlationId, true);
        }
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
